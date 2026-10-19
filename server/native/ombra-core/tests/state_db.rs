//! state.db (design-sync-decision §1–§3): atomic commit of MLS ratchet + outbox ciphertext + replica (crash test that
//! aborts the process between the steps), per-team keys and the D-5 wipe, outbox same-bytes / reseal semantics.
//! The crash cases need `--features failpoints` (they re-run this test binary as a child that aborts at a failpoint).

use std::path::{Path, PathBuf};
use std::process::Command;

use ombra_core::api::call_stateful;
use ombra_core::state::Session;
use serde_json::{json, Value as J};

const KEY: [u8; 32] = [7; 32];
const INSTALL: &[u8] = b"install-1";
const NOW: u64 = 1_800_000_000;

fn hex(b: &[u8]) -> String { b.iter().map(|x| format!("{x:02x}")).collect() }

fn call(s: &mut Session, op: &str, mut a: J) -> J {
    a["op"] = json!(op);
    let r = call_stateful(s, a);
    assert_eq!(r["ok"], json!(true), "{op}: {r}");
    r["result"].clone()
}

fn try_call(s: &mut Session, op: &str, mut a: J) -> J {
    a["op"] = json!(op);
    call_stateful(s, a)
}

struct Fixture { path: PathBuf, team: String, team_key: String, gid: String }

/// One device, one team, the team group (single member: it can still send application messages).
fn setup(dir: &Path) -> Fixture {
    let path = dir.join("state.db");
    let mut s = Session::open(&path, KEY, INSTALL).unwrap();
    call(&mut s, "device.init", json!({"deviceId": hex(&[1; 16]), "memberId": hex(&[2; 16])}));
    let rk = ombra_core::api::call_stateless(json!({"op": "ed25519.keygen"}))["result"]["publicKey"].clone();
    let t = call(&mut s, "team.create", json!({"serverId": hex(&[3; 16]), "recoveryKey": rk, "issuedAt": NOW}));
    let team = t["teamId"].as_str().unwrap().to_string();
    let g = call(&mut s, "group.create", json!({"descriptor": {"type": "team", "teamId": team, "generation": 0}}));
    Fixture { path, team, team_key: t["teamKey"].as_str().unwrap().into(), gid: g["groupId"].as_str().unwrap().into() }
}

fn reopen(f: &Fixture) -> Session {
    let mut s = Session::open(&f.path, KEY, INSTALL).unwrap();
    call(&mut s, "team.unlock", json!({"teamId": f.team, "key": f.team_key}));
    s
}

fn send_item(s: &mut Session, f: &Fixture, item: &str) -> J {
    call(s, "op.send", json!({"groupId": f.gid, "opType": "item.create", "itemId": item, "now": NOW,
        "payload": {"kind": "link", "audience": {"space": format!("hex:{}", f.team)}, "createdAt": NOW, "body": {"url": "https://example.org"}}}))
}

fn epoch(s: &mut Session, f: &Fixture) -> u64 { call(s, "group.info", json!({"groupId": f.gid}))["epoch"].as_u64().unwrap() }
fn outbox(s: &mut Session) -> Vec<J> { call(s, "outbox.list", json!({}))["ops"].as_array().unwrap().clone() }
fn item(s: &mut Session, f: &Fixture, id: &str) -> J { call(s, "item.get", json!({"teamId": f.team, "author": hex(&[2; 16]), "itemId": id})) }

/// Child half of the crash test: open, send one op, the failpoint aborts the process.
#[test]
fn crash_child() {
    let Ok(dir) = std::env::var("OMBRA_CRASH_DIR") else { return };
    let f: J = serde_json::from_str(&std::fs::read_to_string(Path::new(&dir).join("fixture.json")).unwrap()).unwrap();
    let fx = Fixture { path: PathBuf::from(&dir).join("state.db"), team: f["team"].as_str().unwrap().into(),
        team_key: f["key"].as_str().unwrap().into(), gid: f["gid"].as_str().unwrap().into() };
    let mut s = reopen(&fx);
    send_item(&mut s, &fx, &hex(&[9; 16]));
    // Reached only when no failpoint fired.
    std::process::exit(3);
}

fn run_crash(at: &str) -> (Fixture, tempdir::Dir) {
    let d = tempdir::Dir::new();
    let f = setup(d.path());
    std::fs::write(d.path().join("fixture.json"), json!({"team": f.team, "key": f.team_key, "gid": f.gid}).to_string()).unwrap();
    let st = Command::new(std::env::current_exe().unwrap()).args(["--exact", "crash_child", "--nocapture", "--test-threads", "1"])
        .env("OMBRA_CRASH_DIR", d.path()).env("OMBRA_CRASH_AT", at).status().unwrap();
    assert!(!st.success() && st.code() != Some(3), "child must abort at failpoint {at}: {st:?}");
    (f, d)
}

#[cfg(feature = "failpoints")]
#[test]
fn crash_before_commit_keeps_nothing() {
    let (f, _d) = run_crash("before-commit");
    let mut s = reopen(&f);
    assert_eq!(item(&mut s, &f, &hex(&[9; 16])), J::Null, "replica must not hold the op");
    assert!(outbox(&mut s).is_empty(), "outbox must be empty");
    // MLS ratchet rolled back too: the same op goes out again from a consistent state.
    let r = send_item(&mut s, &f, &hex(&[9; 16]));
    assert_eq!(r["seq"], json!(1));
    assert_eq!(outbox(&mut s).len(), 1);
}

#[cfg(feature = "failpoints")]
#[test]
fn crash_after_commit_keeps_everything() {
    let (f, _d) = run_crash("after-commit");
    let mut s = reopen(&f);
    assert_eq!(item(&mut s, &f, &hex(&[9; 16]))["rev"], json!(1), "replica holds the op");
    let ob = outbox(&mut s);
    assert_eq!(ob.len(), 1);
    assert_eq!(ob[0]["sealed"], json!(true), "ciphertext committed with the ratchet step");
    let id = ob[0]["opId"].clone();
    let m1 = call(&mut s, "outbox.seal", json!({"opId": id}))["message"].clone();
    drop(s);
    let mut s = reopen(&f);
    assert_eq!(call(&mut s, "outbox.seal", json!({"opId": id}))["message"], m1, "retry = the same bytes");
    assert_eq!(send_item(&mut s, &f, &hex(&[8; 16]))["seq"], json!(2), "seq continues after the crash");
}

#[test]
fn outbox_same_bytes_then_reseal_on_epoch_conflict() {
    let d = tempdir::Dir::new();
    let f = setup(d.path());
    let mut s = reopen(&f);
    let e = call(&mut s, "outbox.enqueue", json!({"groupId": f.gid, "opType": "item.create", "itemId": hex(&[5; 16]), "now": NOW,
        "payload": {"kind": "link", "audience": {"space": format!("hex:{}", f.team)}, "createdAt": NOW, "body": {"url": "https://a.example"}}}));
    assert_eq!(e["outcome"], json!("applied"), "applied on enqueue, before any network");
    let id = e["opId"].clone();
    assert_eq!(outbox(&mut s)[0]["sealed"], json!(false));
    let m1 = call(&mut s, "outbox.seal", json!({"opId": id}))["message"].clone();
    assert_eq!(call(&mut s, "outbox.seal", json!({"opId": id}))["message"], m1);
    // The server answers 409 epoch-conflict: a commit moved the group on.
    let e0 = epoch(&mut s, &f);
    call(&mut s, "group.update", json!({"groupId": f.gid}));
    assert_eq!(epoch(&mut s, &f), e0 + 1);
    call(&mut s, "outbox.conflict", json!({"opId": id}));
    let r = call(&mut s, "outbox.seal", json!({"opId": id}));
    assert_ne!(r["message"], m1, "resealed under the new epoch");
    assert_eq!(r["opId"], id);
    assert_eq!(r["seq"], e["seq"], "same opId/seq/rev");
    call(&mut s, "outbox.sent", json!({"opId": id}));
    assert!(outbox(&mut s).is_empty());
    // Final server rejection of an own op: dropped + security log.
    let e2 = send_item(&mut s, &f, &hex(&[6; 16]));
    call(&mut s, "outbox.drop", json!({"opId": e2["opId"], "reason": "403 not a member", "now": NOW}));
    let log = call(&mut s, "seclog.list", json!({}))["entries"].clone();
    assert_eq!(log[0]["event"], json!("own-op-rejected"));
}

#[test]
fn team_key_lock_wipe_and_cursor() {
    let d = tempdir::Dir::new();
    let f = setup(d.path());
    let mut s = Session::open(&f.path, KEY, INSTALL).unwrap();
    assert_eq!(call(&mut s, "state.teams", json!({}))["teams"][0]["unlocked"], json!(false));
    assert_eq!(try_call(&mut s, "team.status", json!({"teamId": f.team, "now": NOW}))["code"], json!("team-locked"));
    assert_eq!(try_call(&mut s, "team.unlock", json!({"teamId": f.team, "key": hex(&[0; 32])}))["code"], json!("invalid-input"));
    call(&mut s, "team.unlock", json!({"teamId": f.team, "key": f.team_key}));
    send_item(&mut s, &f, &hex(&[4; 16]));
    // Persistent sync cursor.
    call(&mut s, "sync.set_cursor", json!({"cursor": "c-42", "now": NOW}));
    drop(s);
    let mut s = reopen(&f);
    assert_eq!(call(&mut s, "sync.cursor", json!({}))["cursor"], json!("c-42"));
    // D-5 wipe: every team row leaves state.db.
    call(&mut s, "team.forget", json!({"teamId": f.team}));
    drop(s);
    let c = rusqlite::Connection::open(&f.path).unwrap();
    for t in ["team", "chain", "grp", "item", "outbox", "seen_op", "team_lock"] {
        let n: i64 = c.query_row(&format!("SELECT COUNT(*) FROM {t}"), [], |r| r.get(0)).unwrap();
        assert_eq!(n, 0, "{t} rows left after team.forget");
    }
    let mut s = Session::open(&f.path, KEY, INSTALL).unwrap();
    assert_eq!(call(&mut s, "state.teams", json!({}))["teams"], json!([]));
}

#[test]
fn wrong_state_key_and_clone_are_refused() {
    let d = tempdir::Dir::new();
    let f = setup(d.path());
    assert!(Session::open(&f.path, [8; 32], INSTALL).is_err());
    assert_eq!(Session::open(&f.path, KEY, b"other-install").err().unwrap().code.as_str(), "clone-detected");
}

mod tempdir {
    use std::path::{Path, PathBuf};
    pub struct Dir(PathBuf);
    impl Dir {
        pub fn new() -> Dir {
            let p = std::env::temp_dir().join(format!("ombra-state-db-{}-{}", std::process::id(), ombra_core::util::hex(&ombra_core::util::random::<8>())));
            std::fs::create_dir_all(&p).unwrap();
            Dir(p)
        }
        pub fn path(&self) -> &Path { &self.0 }
    }
    impl Drop for Dir { fn drop(&mut self) { let _ = std::fs::remove_dir_all(&self.0); } }
}
