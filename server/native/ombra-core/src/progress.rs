//! P3 project progress (decision §8): an ephemeral message, not an MLS application message.
//!
//! Signed bytes:
//! `"ombra/v1/progress" ‖ 0x00 ‖ groupId[32] ‖ epoch u64 BE ‖ projectId[16] ‖ memberId[16] ‖ deviceId[16] ‖
//!  status u8 (1 working, 2 waiting, 3 idle) ‖ updated u64 BE (Unix seconds) ‖ u16 len ‖ projectName UTF-8 ‖
//!  u16 len ‖ task UTF-8 (0 = not shared)`,
//! followed by the Ed25519 signature[64] of the device identity key.
//! Wire = `0x01 ‖ epoch u64 BE ‖ nonce[12] ‖ AES-256-GCM(key, nonce, aad, signed ‖ sig)` with
//! `key = MLS-Exporter("ombra/v1/progress", groupId, 32)` of the project group's epoch and
//! `aad = "ombra/v1/progress" ‖ groupId ‖ epoch u64 BE`. The server relays it to online leaves and stores nothing.

use serde_json::{json, Value as J};

use crate::api::Args;
use crate::descriptor::{Descriptor, GroupType};
use crate::ensure;
use crate::mls;
use crate::state::{Provider, State};
use crate::util::{self, e, hex, Code, Id16, Res, H32};

pub const DOMAIN: &[u8] = b"ombra/v1/progress";
pub const EXPORTER_LABEL: &str = "ombra/v1/progress";
pub const WIRE_VERSION: u8 = 1;
/// Longest task title and project name in bytes; longer ones are cut at a character boundary by `seal`.
pub const MAX_TEXT: usize = 200;
/// Clock skew accepted for `updated` in the future.
pub const MAX_SKEW: u64 = 300;
/// Age after which a message is refused as a replay: a sender re-seals its current progress (with a fresh `updated`)
/// whenever a device comes online, so only a recorded old message can be older.
pub const MAX_AGE: u64 = 300;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Status { Working = 1, Waiting = 2, Idle = 3 }

impl Status {
    pub fn parse(s: &str) -> Res<Status> {
        match s { "working" => Ok(Status::Working), "waiting" => Ok(Status::Waiting), "idle" => Ok(Status::Idle),
            _ => Err(e(Code::InvalidInput, format!("unknown progress status '{s}'"))) }
    }
    pub fn as_str(self) -> &'static str { match self { Status::Working => "working", Status::Waiting => "waiting", Status::Idle => "idle" } }
    fn from_u8(b: u8) -> Res<Status> {
        match b { 1 => Ok(Status::Working), 2 => Ok(Status::Waiting), 3 => Ok(Status::Idle), _ => Err(e(Code::InvalidInput, "bad progress status")) }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Progress {
    pub group_id: H32,
    pub epoch: u64,
    pub project_id: Id16,
    pub member_id: Id16,
    pub device_id: Id16,
    pub status: Status,
    pub updated: u64,
    pub project_name: String,
    pub task: Option<String>,
}

const FIXED: usize = 32 + 8 + 16 * 3 + 1 + 8;

fn put_text(v: &mut Vec<u8>, s: &str) {
    v.extend_from_slice(&(s.len() as u16).to_be_bytes());
    v.extend_from_slice(s.as_bytes());
}

fn take_text<'a>(f: &'a [u8], at: &mut usize) -> Res<&'a str> {
    ensure!(f.len() >= *at + 2, Code::InvalidInput, "progress text truncated");
    let len = u16::from_be_bytes([f[*at], f[*at + 1]]) as usize;
    ensure!(len <= MAX_TEXT && f.len() >= *at + 2 + len, Code::InvalidInput, "progress text length mismatch");
    let s = std::str::from_utf8(&f[*at + 2..*at + 2 + len]).map_err(|_| e(Code::InvalidInput, "progress text is not UTF-8"))?;
    *at += 2 + len;
    Ok(s)
}

impl Progress {
    pub fn signed_bytes(&self) -> Vec<u8> {
        let task = self.task.as_deref().unwrap_or("");
        let mut v = Vec::with_capacity(DOMAIN.len() + 1 + FIXED + 4 + self.project_name.len() + task.len());
        v.extend_from_slice(DOMAIN);
        v.push(0);
        v.extend_from_slice(&self.group_id);
        v.extend_from_slice(&self.epoch.to_be_bytes());
        v.extend_from_slice(&self.project_id);
        v.extend_from_slice(&self.member_id);
        v.extend_from_slice(&self.device_id);
        v.push(self.status as u8);
        v.extend_from_slice(&self.updated.to_be_bytes());
        put_text(&mut v, &self.project_name);
        put_text(&mut v, task);
        v
    }

    /// Splits `signed ‖ sig`; the signature is not checked here.
    pub fn parse(b: &[u8]) -> Res<(Progress, &[u8], [u8; 64])> {
        let head = DOMAIN.len() + 1;
        ensure!(b.len() >= head + FIXED + 4 + 64 && &b[..DOMAIN.len()] == DOMAIN && b[DOMAIN.len()] == 0, Code::InvalidInput, "not a progress message");
        let (signed, sig) = b.split_at(b.len() - 64);
        let f = &signed[head..];
        let id16 = |o: usize| -> Id16 { f[o..o + 16].try_into().unwrap() };
        let u64be = |o: usize| u64::from_be_bytes(f[o..o + 8].try_into().unwrap());
        let mut at = FIXED;
        let project_name = take_text(f, &mut at)?.to_string();
        let task = take_text(f, &mut at)?;
        ensure!(at == f.len(), Code::InvalidInput, "trailing bytes in progress message");
        let p = Progress { group_id: f[..32].try_into().unwrap(), epoch: u64be(32), project_id: id16(40), member_id: id16(56), device_id: id16(72),
            status: Status::from_u8(f[88])?, updated: u64be(89), project_name, task: (!task.is_empty()).then(|| task.to_string()) };
        Ok((p, signed, sig.try_into().unwrap()))
    }

    pub fn to_json(&self) -> J {
        json!({"groupId": hex(&self.group_id), "epoch": self.epoch, "projectId": hex(&self.project_id), "projectName": self.project_name,
            "memberId": hex(&self.member_id), "deviceId": hex(&self.device_id), "status": self.status.as_str(), "updated": self.updated, "task": self.task})
    }
}

pub fn aad(group_id: &H32, epoch: u64) -> Vec<u8> {
    let mut v = DOMAIN.to_vec();
    v.extend_from_slice(group_id);
    v.extend_from_slice(&epoch.to_be_bytes());
    v
}

/// Signs with the device identity key and encrypts with the exporter key of `p.epoch`.
pub fn seal_wire(key: &[u8; 32], identity_sk: &[u8; 32], p: &Progress, nonce: [u8; 12]) -> Vec<u8> {
    let mut pt = p.signed_bytes();
    let sig = util::ed25519_sign(identity_sk, &pt);
    pt.extend_from_slice(&sig);
    let mut w = vec![WIRE_VERSION];
    w.extend_from_slice(&p.epoch.to_be_bytes());
    w.extend_from_slice(&nonce);
    w.extend(util::aes_gcm_seal(key, &nonce, &aad(&p.group_id, p.epoch), &pt));
    w
}

pub fn wire_epoch(w: &[u8]) -> Res<u64> {
    ensure!(w.len() > 1 + 8 + 12 + 16 && w[0] == WIRE_VERSION, Code::InvalidInput, "not a progress wire message");
    Ok(u64::from_be_bytes(w[1..9].try_into().unwrap()))
}

/// Decrypts with the exporter key of (`group_id`, epoch in the wire) and checks that the plaintext names the same
/// group and epoch. Returns the payload and the signed bytes + signature for the caller's key lookup.
pub fn open_wire(key: &[u8; 32], group_id: &H32, w: &[u8]) -> Res<(Progress, Vec<u8>, [u8; 64])> {
    let epoch = wire_epoch(w)?;
    let nonce: [u8; 12] = w[9..21].try_into().unwrap();
    let pt = util::aes_gcm_open(key, &nonce, &aad(group_id, epoch), &w[21..]).ok_or_else(|| e(Code::Mls, "progress decryption failed"))?;
    let (p, signed, sig) = Progress::parse(&pt)?;
    ensure!(p.group_id == *group_id && p.epoch == epoch, Code::InvalidInput, "progress names another group or epoch");
    Ok((p.clone(), signed.to_vec(), sig))
}

fn truncate(s: &str) -> Option<String> {
    let s = s.trim();
    if s.is_empty() { return None; }
    let mut end = s.len().min(MAX_TEXT);
    while !s.is_char_boundary(end) { end -= 1; }
    Some(s[..end].to_string())
}

/// Project group usable for progress: ours, open, of type project. Returns its projectId.
fn project_of(st: &State, gid: &H32) -> Res<Id16> {
    let g = st.group(gid)?;
    ensure!(!g.closed && !g.evicted, Code::ClosedGeneration, "group is closed or we were removed");
    ensure!(g.kind == GroupType::Project, Code::InvalidInput, "progress is published only in project groups");
    Descriptor::parse(&g.descriptor)?.project_id.ok_or_else(|| e(Code::InvalidInput, "project group without projectId"))
}

fn exporter_key(p: &Provider, gid: &H32) -> Res<[u8; 32]> {
    Ok(mls::export_secret(p, gid, EXPORTER_LABEL, gid, 32)?.try_into().unwrap())
}

/// `progress.seal {groupId, status, projectName, task?, now}` → `{ciphertext, epoch}` for `POST /v1/groups/{gid}/ops`
/// with `ephemeral = true`.
pub fn seal(st: &State, p: &Provider, a: &Args) -> Res<J> {
    let gid = a.h("groupId")?;
    let project_id = project_of(st, &gid)?;
    let d = st.device()?;
    let epoch = mls::load_group(p, &gid)?.epoch().as_u64();
    let msg = Progress { group_id: gid, epoch, project_id, member_id: d.member_id, device_id: d.device_id,
        status: Status::parse(a.str("status")?)?, updated: a.u("now")?, project_name: a.opt_str("projectName").and_then(truncate).unwrap_or_default(),
        task: a.opt_str("task").and_then(truncate) };
    let w = seal_wire(&exporter_key(p, &gid)?, &d.identity_sk, &msg, util::random::<12>());
    Ok(json!({"ciphertext": hex(&w), "epoch": epoch}))
}

/// `progress.open {groupId, ciphertext, senderDeviceId, now}` → the verified payload. Refused unless: the message is
/// for our current epoch of this project group, decrypts with its exporter key, the signer is the relaying device,
/// a leaf of the group, an active device of the named member at the verified roster head, the signature verifies
/// against its certified identity key, and `updated` is neither in the future nor older than [`MAX_AGE`].
pub fn open(st: &State, p: &Provider, a: &Args) -> Res<J> {
    let gid = a.h("groupId")?;
    let project_id = project_of(st, &gid)?;
    let w = a.bytes("ciphertext")?;
    let group = mls::load_group(p, &gid)?;
    let epoch = wire_epoch(&w)?;
    ensure!(epoch == group.epoch().as_u64(), Code::Mls, "progress for epoch {epoch}, ours is {}", group.epoch().as_u64());
    let (msg, signed, sig) = open_wire(&exporter_key(p, &gid)?, &gid, &w)?;
    ensure!(msg.project_id == project_id, Code::InvalidInput, "progress names another project");
    ensure!(msg.device_id == a.id("senderDeviceId")?, Code::BadSignature, "progress signer is not the relaying device");
    ensure!(group.members().any(|m| m.credential.serialized_content() == msg.device_id.as_slice()), Code::BadSignature,
        "progress signer is not a leaf of the group");
    let team = st.team(&st.group(&gid)?.team_id)?;
    let (member, _) = team.chain.head().active_device(&msg.device_id).ok_or_else(|| e(Code::BadSignature, "progress signer is not an active device"))?;
    ensure!(member.member_id == msg.member_id, Code::BadSignature, "progress member does not own the signing device");
    let cert = team.chain.cert(&msg.device_id).ok_or_else(|| e(Code::BadSignature, "no certificate for the progress signer"))?;
    ensure!(util::ed25519_verify(&cert.identity_key, &signed, &sig), Code::BadSignature, "bad progress signature");
    let now = a.u("now")?;
    ensure!(msg.updated <= now + MAX_SKEW, Code::InvalidInput, "progress time is in the future");
    ensure!(msg.updated + MAX_AGE >= now, Code::InvalidInput, "progress is older than {MAX_AGE} s: a replay");
    Ok(msg.to_json())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample(task: Option<&str>) -> Progress {
        Progress { group_id: [7; 32], epoch: 4, project_id: [1; 16], member_id: [2; 16], device_id: [3; 16], status: Status::Waiting,
            updated: 1_760_000_000, project_name: "Ombra".into(), task: task.map(str::to_string) }
    }

    fn sk() -> [u8; 32] { [9; 32] }

    #[test]
    fn round_trip_with_and_without_task() {
        for t in [None, Some("Fix login form — шаг 3")] {
            let p = sample(t);
            let w = seal_wire(&[5; 32], &sk(), &p, [1; 12]);
            assert_eq!(wire_epoch(&w).unwrap(), 4);
            let (q, signed, sig) = open_wire(&[5; 32], &p.group_id, &w).unwrap();
            assert_eq!(q, p);
            assert!(util::ed25519_verify(&util::ed25519_public(&sk()), &signed, &sig));
        }
    }

    #[test]
    fn plaintext_fields_are_not_visible_on_the_wire() {
        let w = seal_wire(&[5; 32], &sk(), &sample(Some("secret task title")), [1; 12]);
        assert!(!w.windows(6).any(|x| x == b"secret"));
        assert!(!w.windows(16).any(|x| x == [2u8; 16]));
    }

    #[test]
    fn wrong_key_group_or_tampering_is_refused() {
        let p = sample(Some("t"));
        let w = seal_wire(&[5; 32], &sk(), &p, [1; 12]);
        assert!(open_wire(&[6; 32], &p.group_id, &w).is_err(), "other epoch/group key");
        assert!(open_wire(&[5; 32], &[8; 32], &w).is_err(), "aad binds the group");
        let mut t = w.clone();
        *t.last_mut().unwrap() ^= 1;
        assert!(open_wire(&[5; 32], &p.group_id, &t).is_err(), "ciphertext tag");
        let mut e = w.clone();
        e[8] ^= 1;
        assert!(open_wire(&[5; 32], &p.group_id, &e).is_err(), "aad binds the epoch");
    }

    #[test]
    fn signature_covers_every_field() {
        let p = sample(Some("t"));
        let pk = util::ed25519_public(&sk());
        let b = p.signed_bytes();
        let sig = util::ed25519_sign(&sk(), &b);
        let mut whole = b.clone();
        whole.extend_from_slice(&sig);
        let (q, signed, s) = Progress::parse(&whole).unwrap();
        assert_eq!((q, signed, s), (p, b.as_slice(), sig));
        for i in DOMAIN.len() + 1..b.len() {
            let mut t = b.clone();
            t[i] ^= 1;
            assert!(!util::ed25519_verify(&pk, &t, &sig), "byte {i}");
        }
    }

    #[test]
    fn parse_rejects_bad_status_length_and_domain() {
        let mut b = sample(None).signed_bytes();
        b.extend_from_slice(&[0; 64]);
        let st = DOMAIN.len() + 1 + 88;
        let mut s = b.clone(); s[st] = 9;
        assert!(Progress::parse(&s).is_err());
        let mut d = b.clone(); d[0] = b'x';
        assert!(Progress::parse(&d).is_err());
        let mut l = b.clone(); l.insert(DOMAIN.len() + 1 + FIXED, b'a');
        assert!(Progress::parse(&l).is_err());
        assert!(Progress::parse(&b[..b.len() - 1]).is_err());
    }

    #[test]
    fn task_is_trimmed_and_cut_at_a_char_boundary() {
        assert_eq!(truncate("  "), None);
        let long = "я".repeat(150); // 300 bytes
        let t = truncate(&long).unwrap();
        assert!(t.len() <= MAX_TEXT && t.chars().all(|c| c == 'я'));
    }

    #[test]
    fn status_names() {
        for s in ["working", "waiting", "idle"] { assert_eq!(Status::parse(s).unwrap().as_str(), s); }
        assert!(Status::parse("error").is_err());
    }

    /// Through the JSON command layer on a real state.db: a project group's exporter key, the device signature and
    /// every acceptance check of `progress.open`.
    #[test]
    fn seal_and_open_in_a_real_project_group() {
        use crate::api::call_stateful;
        use crate::state::Session;
        let dir = std::env::temp_dir().join(format!("ombra-progress-{}", hex(&util::random::<8>())));
        std::fs::create_dir_all(&dir).unwrap();
        let mut s = Session::open(&dir.join("state.db"), [3; 32], b"install").unwrap();
        let mut call = |op: &str, mut args: J| -> J {
            args["op"] = json!(op);
            call_stateful(&mut s, args)
        };
        let ok = |r: J| -> J { assert_eq!(r["ok"], json!(true), "{r}"); r["result"].clone() };
        let (dev, member) = (hex(&[0x11; 16]), hex(&[0x22; 16]));
        ok(call("device.init", json!({"deviceId": dev, "memberId": member})));
        let team = ok(call("team.create", json!({"serverId": hex(&[5; 16]), "issuedAt": 1, "recoveryKey": hex(&[6; 32]), "pins": []})))["teamId"].clone();
        let project = hex(&[0x33; 16]);
        ok(call("roster.update", json!({"teamId": team, "now": 10, "apply": true, "grantAccess": [{"projectId": project, "memberId": member}]})));
        let gid = ok(call("group.create", json!({"descriptor": {"type": "project", "teamId": team, "projectId": project, "generation": 0}})))["groupId"].clone();

        let sealed = ok(call("progress.seal", json!({"groupId": gid, "status": "working", "projectName": "Ombra", "task": "Step 1", "now": 100})));
        let c = sealed["ciphertext"].clone();
        let opened = ok(call("progress.open", json!({"groupId": gid, "ciphertext": c, "senderDeviceId": dev, "now": 100})));
        assert_eq!((opened["status"].clone(), opened["task"].clone(), opened["projectName"].clone(), opened["memberId"].clone(), opened["projectId"].clone()),
            (json!("working"), json!("Step 1"), json!("Ombra"), json!(member), json!(project)));

        let refused = |r: J| assert_eq!(r["ok"], json!(false), "{r}");
        refused(call("progress.open", json!({"groupId": gid, "ciphertext": c, "senderDeviceId": hex(&[0x44; 16]), "now": 100})));
        let future = ok(call("progress.seal", json!({"groupId": gid, "status": "idle", "now": 100 + MAX_SKEW + 1})))["ciphertext"].clone();
        refused(call("progress.open", json!({"groupId": gid, "ciphertext": future, "senderDeviceId": dev, "now": 100})));
        // a replayed message older than the freshness window is refused
        refused(call("progress.open", json!({"groupId": gid, "ciphertext": c, "senderDeviceId": dev, "now": 100 + MAX_AGE + 1})));
        ok(call("progress.open", json!({"groupId": gid, "ciphertext": c, "senderDeviceId": dev, "now": 100 + MAX_AGE})));
        let mut bad = util::unhex(c.as_str().unwrap()).unwrap();
        let n = bad.len();
        bad[n - 1] ^= 1;
        refused(call("progress.open", json!({"groupId": gid, "ciphertext": hex(&bad), "senderDeviceId": dev, "now": 100})));
        // a signature by a key that is not the device's certified identity key, sealed with the right exporter key
        let key: [u8; 32] = util::unhex(ok(call("group.export", json!({"groupId": gid, "label": EXPORTER_LABEL, "context": gid})))["secret"].as_str().unwrap())
            .unwrap().try_into().unwrap();
        let gid32: H32 = util::unhex(gid.as_str().unwrap()).unwrap().try_into().unwrap();
        let (msg, _, _) = open_wire(&key, &gid32, &util::unhex(c.as_str().unwrap()).unwrap()).unwrap();
        let forged = seal_wire(&key, &[0x77; 32], &msg, [2; 12]);
        refused(call("progress.open", json!({"groupId": gid, "ciphertext": hex(&forged), "senderDeviceId": dev, "now": 100})));
        // only project groups carry progress
        let tg = ok(call("group.create", json!({"descriptor": {"type": "team", "teamId": team, "generation": 0}})))["groupId"].clone();
        refused(call("progress.seal", json!({"groupId": tg, "status": "idle", "now": 100})));
        let _ = std::fs::remove_dir_all(&dir);
    }
}
