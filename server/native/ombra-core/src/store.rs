//! `state.db` (SQLite via rusqlite, bundled): the device's persistent team state, encrypted row by row.
//!
//! Keys:
//! - device rows (`meta`, `mls_kv`, `seclog`) are sealed with the state key from the OS key store;
//! - team rows (`team`, `chain`, `grp`, `item`, `status`, `share`, `xfer`, `outbox`) are sealed with that team's own key,
//!   also kept in the OS key store by the caller (§3.2 «ключ реплики — отдельный для каждой команды»). `team.forget`
//!   deletes every row of the team (secure_delete=ON, WAL truncated) and the caller destroys the team key: copies of
//!   the file taken earlier cannot be opened for that team any more (D-5).
//!
//! Row format: `v = nonce[12] ‖ AES-256-GCM(key, nonce, aad = "ombra/row/v1" ‖ 0 ‖ table ‖ 0 ‖ primary key, plaintext)`.
//! Only random ids, enums, `seq` and times stay in plaintext columns (needed for lookups and ordering).
//!
//! Every stateful call runs in one SQLite transaction (`Session::transact`): MLS storage changes, replica, outbox,
//! delivery receipts and cursor commit together or not at all.

use std::cell::RefCell;
use std::collections::HashMap;
use std::path::Path;

use rusqlite::{params, Connection, OptionalExtension};
use zeroize::Zeroizing;

use crate::util::{self, e, err, hex, Code, Res, H32};

pub type Key = Zeroizing<[u8; 32]>;

/// Dedup window of delivered operations: server queue TTL (30 days, D-7) + 7 days margin.
pub const DEDUP_WINDOW_SECS: u64 = 37 * 24 * 3600;

const SCHEMA: &str = "
CREATE TABLE IF NOT EXISTS meta     (name TEXT PRIMARY KEY, v BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS mls_kv   (h BLOB PRIMARY KEY, v BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS team_lock(team_id BLOB PRIMARY KEY, chk BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS team     (team_id BLOB PRIMARY KEY, v BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS chain    (team_id BLOB PRIMARY KEY, v BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS grp      (group_id BLOB PRIMARY KEY, team_id BLOB NOT NULL, v BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS item     (team_id BLOB NOT NULL, ikey BLOB NOT NULL, created_at INTEGER NOT NULL, v BLOB NOT NULL, PRIMARY KEY (team_id, ikey));
CREATE TABLE IF NOT EXISTS status   (team_id BLOB NOT NULL, ikey BLOB NOT NULL, recipient BLOB NOT NULL, v BLOB NOT NULL, PRIMARY KEY (team_id, ikey, recipient));
CREATE TABLE IF NOT EXISTS share    (team_id BLOB NOT NULL, share_id BLOB NOT NULL, v BLOB NOT NULL, PRIMARY KEY (team_id, share_id));
CREATE TABLE IF NOT EXISTS xfer     (team_id BLOB NOT NULL, transfer_id BLOB NOT NULL, v BLOB NOT NULL, PRIMARY KEY (team_id, transfer_id));
CREATE TABLE IF NOT EXISTS outbox   (op_id BLOB PRIMARY KEY, team_id BLOB NOT NULL, ord INTEGER NOT NULL, state INTEGER NOT NULL, v BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS seqctr   (team_id BLOB NOT NULL, home TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (team_id, home));
CREATE TABLE IF NOT EXISTS seen_op  (group_id BLOB NOT NULL, sender BLOB NOT NULL, op_id BLOB NOT NULL, team_id BLOB NOT NULL,
                                     seq INTEGER NOT NULL, ct BLOB NOT NULL, ok INTEGER NOT NULL, at INTEGER NOT NULL,
                                     PRIMARY KEY (group_id, sender, op_id));
CREATE TABLE IF NOT EXISTS seen_msg (h BLOB PRIMARY KEY, team_id BLOB NOT NULL, at INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS seen_msg_at ON seen_msg(at);
CREATE INDEX IF NOT EXISTS seen_seq ON seen_op(group_id, sender, seq);
CREATE INDEX IF NOT EXISTS seen_at  ON seen_op(at);
CREATE INDEX IF NOT EXISTS outbox_ord ON outbox(state, ord);
CREATE TABLE IF NOT EXISTS seclog   (id INTEGER PRIMARY KEY AUTOINCREMENT, team_id BLOB, at INTEGER NOT NULL, v BLOB NOT NULL);
";

/// Team tables wiped by `team.forget` (all have a `team_id` column).
const TEAM_TABLES: &[&str] = &["team", "chain", "grp", "item", "status", "share", "xfer", "outbox", "seqctr", "seen_op", "seen_msg", "seclog", "team_lock"];

const TEAM_CHECK: &[u8] = b"ombra/team-key-check/v1";

pub fn sq(x: rusqlite::Error) -> util::Error { e(Code::Storage, format!("state.db: {x}")) }

fn aad(table: &str, pk: &[u8]) -> Vec<u8> {
    let mut a = b"ombra/row/v1\0".to_vec();
    a.extend_from_slice(table.as_bytes());
    a.push(0);
    a.extend_from_slice(pk);
    a
}

pub fn seal(key: &[u8; 32], table: &str, pk: &[u8], pt: &[u8]) -> Vec<u8> {
    let nonce = util::random::<12>();
    let mut v = nonce.to_vec();
    v.extend(util::aes_gcm_seal(key, &nonce, &aad(table, pk), pt));
    v
}

pub fn open(key: &[u8; 32], table: &str, pk: &[u8], v: &[u8]) -> Res<Zeroizing<Vec<u8>>> {
    if v.len() < 28 { return err(Code::Storage, format!("{table}: truncated row")); }
    let nonce: [u8; 12] = v[..12].try_into().unwrap();
    util::aes_gcm_open(key, &nonce, &aad(table, pk), &v[12..]).map(Zeroizing::new)
        .ok_or_else(|| e(Code::Storage, format!("{table}: row decryption failed (wrong key or tampered row)")))
}

pub fn open_conn(path: &Path) -> Res<Connection> {
    let c = Connection::open(path).map_err(sq)?;
    c.busy_timeout(std::time::Duration::from_secs(5)).map_err(sq)?;
    c.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON; PRAGMA foreign_keys=ON;").map_err(sq)?;
    c.execute_batch(SCHEMA).map_err(sq)?;
    Ok(c)
}

/// The open transaction of one call: SQL access plus the keys.
pub struct Db<'a> {
    pub conn: &'a Connection,
    pub(crate) dev_key: &'a [u8; 32],
    pub(crate) team_keys: &'a RefCell<HashMap<H32, Key>>,
}

impl<'a> Db<'a> {
    pub fn team_key(&self, team: &H32) -> Res<[u8; 32]> {
        self.team_keys.borrow().get(team).map(|k| **k)
            .ok_or_else(|| e(Code::TeamLocked, format!("team {} is locked (team.unlock with its key first)", hex(team))))
    }
    pub fn is_unlocked(&self, team: &H32) -> bool { self.team_keys.borrow().contains_key(team) }

    /// Register a new team's key (team.create / team.import): stores the check row.
    pub fn add_team_key(&self, team: &H32, key: [u8; 32]) -> Res<()> {
        if let Some(k) = self.team_keys.borrow().get(team) {
            return if util::ct_eq(&**k, &key) { Ok(()) } else { err(Code::InvalidInput, "team already has another key") };
        }
        self.conn.execute("INSERT OR REPLACE INTO team_lock (team_id, chk) VALUES (?1, ?2)",
            params![&team[..], seal(&key, "team_lock", team, TEAM_CHECK)]).map_err(sq)?;
        self.team_keys.borrow_mut().insert(*team, Zeroizing::new(key));
        Ok(())
    }

    // ---- generic sealed rows ----

    pub fn dev_seal(&self, table: &str, pk: &[u8], pt: &[u8]) -> Vec<u8> { seal(self.dev_key, table, pk, pt) }
    pub fn dev_open(&self, table: &str, pk: &[u8], v: &[u8]) -> Res<Zeroizing<Vec<u8>>> { open(self.dev_key, table, pk, v) }
    pub fn team_seal(&self, team: &H32, table: &str, pk: &[u8], pt: &[u8]) -> Res<Vec<u8>> { Ok(seal(&self.team_key(team)?, table, pk, pt)) }
    pub fn team_open(&self, team: &H32, table: &str, pk: &[u8], v: &[u8]) -> Res<Zeroizing<Vec<u8>>> { open(&self.team_key(team)?, table, pk, v) }

    pub fn meta_get(&self, name: &str) -> Res<Option<Zeroizing<Vec<u8>>>> {
        let v: Option<Vec<u8>> = self.conn.query_row("SELECT v FROM meta WHERE name = ?1", [name], |r| r.get(0)).optional().map_err(sq)?;
        v.map(|v| self.dev_open("meta", name.as_bytes(), &v)).transpose()
    }
    pub fn meta_put(&self, name: &str, pt: &[u8]) -> Res<()> {
        self.conn.execute("INSERT OR REPLACE INTO meta (name, v) VALUES (?1, ?2)", params![name, self.dev_seal("meta", name.as_bytes(), pt)]).map_err(sq)?;
        Ok(())
    }

    /// Team-keyed row of a `(team_id, k) → v` table.
    pub fn trow_get(&self, table: &'static str, kcol: &'static str, team: &H32, k: &[u8]) -> Res<Option<Zeroizing<Vec<u8>>>> {
        let v: Option<Vec<u8>> = self.conn.query_row(&format!("SELECT v FROM {table} WHERE team_id = ?1 AND {kcol} = ?2"), params![&team[..], k], |r| r.get(0))
            .optional().map_err(sq)?;
        v.map(|v| self.team_open(team, table, &[&team[..], k].concat(), &v)).transpose()
    }
    pub fn trow_put(&self, table: &'static str, kcol: &'static str, team: &H32, k: &[u8], pt: &[u8]) -> Res<()> {
        let v = self.team_seal(team, table, &[&team[..], k].concat(), pt)?;
        self.conn.execute(&format!("INSERT OR REPLACE INTO {table} (team_id, {kcol}, v) VALUES (?1, ?2, ?3)"), params![&team[..], k, v]).map_err(sq)?;
        Ok(())
    }

    // ---- delivery receipts (dedup before decryption) ----

    /// `(ciphertext hash, applied)` of a delivered `(group, sender, opId)`, if seen inside the window.
    pub fn seen_get(&self, gid: &H32, sender: &[u8], op_id: &[u8]) -> Res<Option<(Vec<u8>, bool)>> {
        self.conn.query_row("SELECT ct, ok FROM seen_op WHERE group_id = ?1 AND sender = ?2 AND op_id = ?3", params![&gid[..], sender, op_id],
            |r| Ok((r.get::<_, Vec<u8>>(0)?, r.get::<_, i64>(1)? != 0))).optional().map_err(sq)
    }
    pub fn seen_seq(&self, gid: &H32, sender: &[u8], seq: u64) -> Res<bool> {
        if seq == 0 { return Ok(false); }
        self.conn.query_row("SELECT 1 FROM seen_op WHERE group_id = ?1 AND sender = ?2 AND seq = ?3 AND ok = 1 LIMIT 1",
            params![&gid[..], sender, seq as i64], |_| Ok(())).optional().map_err(sq).map(|x| x.is_some())
    }
    #[allow(clippy::too_many_arguments)]
    pub fn seen_put(&self, gid: &H32, team: &H32, sender: &[u8], op_id: &[u8], seq: u64, ct_hash: &[u8], ok: bool, at: u64) -> Res<()> {
        self.conn.execute("INSERT OR REPLACE INTO seen_op (group_id, sender, op_id, team_id, seq, ct, ok, at) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            params![&gid[..], sender, op_id, &team[..], seq as i64, ct_hash, ok as i64, at as i64]).map_err(sq)?;
        Ok(())
    }
    /// Drop receipts older than [`DEDUP_WINDOW_SECS`]; the server deletes queue rows after 30 days.
    pub fn seen_prune(&self, now: u64) -> Res<usize> {
        let cut = now.saturating_sub(DEDUP_WINDOW_SECS) as i64;
        let n = self.conn.execute("DELETE FROM seen_op WHERE at < ?1", [cut]).map_err(sq)?;
        Ok(n + self.conn.execute("DELETE FROM seen_msg WHERE at < ?1", [cut]).map_err(sq)?)
    }
    /// Delivered MLS message (hash of its bytes) already committed: redelivery is acked without decrypting.
    pub fn msg_seen(&self, h: &H32) -> Res<bool> {
        self.conn.query_row("SELECT 1 FROM seen_msg WHERE h = ?1", [&h[..]], |_| Ok(())).optional().map_err(sq).map(|x| x.is_some())
    }
    pub fn msg_put(&self, h: &H32, team: &H32, at: u64) -> Res<()> {
        self.conn.execute("INSERT OR REPLACE INTO seen_msg (h, team_id, at) VALUES (?1, ?2, ?3)", params![&h[..], &team[..], at as i64]).map_err(sq)?;
        Ok(())
    }

    // ---- security log ----

    pub fn seclog_add(&self, team: Option<&H32>, at: u64, entry: &serde_json::Value) -> Res<()> {
        let pt = serde_json::to_vec(entry).map_err(|x| e(Code::Internal, x.to_string()))?;
        self.conn.execute("INSERT INTO seclog (team_id, at, v) VALUES (?1, ?2, ?3)",
            params![team.map(|t| t.to_vec()), at as i64, self.dev_seal("seclog", b"", &pt)]).map_err(sq)?;
        Ok(())
    }
    pub fn seclog_list(&self, team: Option<&H32>, limit: u64) -> Res<Vec<serde_json::Value>> {
        let mut q = self.conn.prepare("SELECT id, team_id, at, v FROM seclog WHERE (?1 IS NULL OR team_id = ?1) ORDER BY id DESC LIMIT ?2").map_err(sq)?;
        let rows = q.query_map(params![team.map(|t| t.to_vec()), limit as i64], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, Option<Vec<u8>>>(1)?, r.get::<_, i64>(2)?, r.get::<_, Vec<u8>>(3)?)))
            .map_err(sq)?;
        let mut out = vec![];
        for row in rows {
            let (id, t, at, v) = row.map_err(sq)?;
            let mut j: serde_json::Value = serde_json::from_slice(&self.dev_open("seclog", b"", &v)?).map_err(|x| e(Code::Storage, x.to_string()))?;
            j["id"] = id.into();
            j["at"] = at.into();
            j["teamId"] = t.map(|t| serde_json::Value::String(hex(&t))).unwrap_or(serde_json::Value::Null);
            out.push(j);
        }
        Ok(out)
    }

    // ---- per-group sequence counters of this member (R5-08) ----

    pub fn seq_counter(&self, team: &H32, home: &str) -> Res<u64> {
        let v: Option<i64> = self.conn.query_row("SELECT seq FROM seqctr WHERE team_id = ?1 AND home = ?2", params![&team[..], home], |r| r.get(0)).optional().map_err(sq)?;
        Ok(v.unwrap_or(0) as u64)
    }
    pub fn seq_set(&self, team: &H32, home: &str, seq: u64) -> Res<()> {
        self.conn.execute("INSERT OR REPLACE INTO seqctr (team_id, home, seq) VALUES (?1, ?2, ?3)", params![&team[..], home, seq as i64]).map_err(sq)?;
        Ok(())
    }

    /// D-5: every row of the team, including its key check row.
    pub fn wipe_team(&self, team: &H32) -> Res<()> {
        for t in TEAM_TABLES {
            self.conn.execute(&format!("DELETE FROM {t} WHERE team_id = ?1"), [&team[..]]).map_err(sq)?;
        }
        self.team_keys.borrow_mut().remove(team);
        Ok(())
    }

    /// Run `f` inside a SAVEPOINT: on error only its own SQL changes are undone.
    pub fn savepoint<T>(&self, f: impl FnOnce() -> Res<T>) -> Res<T> {
        self.conn.execute_batch("SAVEPOINT op").map_err(sq)?;
        match f() {
            Ok(v) => { self.conn.execute_batch("RELEASE op").map_err(sq)?; Ok(v) }
            Err(x) => { self.conn.execute_batch("ROLLBACK TO op; RELEASE op").map_err(sq)?; Err(x) }
        }
    }
}

/// Test-only crash injection (`--features failpoints`): `OMBRA_CRASH_AT=<name>` aborts the process at that point,
/// like a power loss — no destructors, no rollback.
#[inline]
pub fn failpoint(_name: &str) {
    #[cfg(feature = "failpoints")]
    if std::env::var("OMBRA_CRASH_AT").is_ok_and(|v| v == _name) { std::process::abort(); }
}

pub fn check_team_key(conn: &Connection, team: &H32, key: &[u8; 32]) -> Res<bool> {
    let chk: Option<Vec<u8>> = conn.query_row("SELECT chk FROM team_lock WHERE team_id = ?1", [&team[..]], |r| r.get(0)).optional().map_err(sq)?;
    let chk = chk.ok_or_else(|| e(Code::NotFound, format!("unknown team {}", hex(team))))?;
    Ok(open(key, "team_lock", team, &chk).is_ok_and(|p| p.as_slice() == TEAM_CHECK))
}
