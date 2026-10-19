//! Persistent device state (§2.1 SD-10) on top of `state.db` (see `store.rs`): OpenMLS storage (signature key, HPKE
//! leaf/init keys, epoch secrets), the identity key, verified roster chains, group metadata. The replica, outbox,
//! delivery receipts and the security log live in their own tables (`replica.rs`, `outbox.rs`) and are never loaded
//! as a whole.
//!
//! Every call runs in one SQLite transaction (`Session::transact`): on error the SQL changes are rolled back and the
//! in-memory state *and* the MLS storage are restored (rejected input never changes state) — except
//! `Code::Equivocation`, whose freeze must persist. On success only the rows that changed are written: device/team/group
//! rows whose plaintext hash differs from the last persisted one, the chain row when its fingerprint changed, and the
//! diff of the MLS key-value storage. Nothing is rewritten as a whole.
//!
//! `installId` is sealed inside: opening with another installId fails with `clone-detected` (profile restored onto
//! another machine → re-pairing required). Team rows are sealed with per-team keys supplied by the caller
//! (`team.unlock`); until then the team is `locked`.

use std::cell::RefCell;
use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::path::Path;

use openmls_memory_storage::MemoryStorage;
use openmls_rust_crypto::RustCrypto;
use openmls_traits::OpenMlsProvider;
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use zeroize::{Zeroize, Zeroizing};

use crate::replica::GroupTrack;
use crate::roster::{Chain, Role};
use crate::store::{self, sq, Db, Key};
use crate::util::{self, e, err, hex, hexser, Code, Error, Id16, Res, H32};
use crate::descriptor::GroupType;
use crate::ensure;

const FORMAT: &[u8] = b"ombra-state-db/2";

#[derive(Default)]
pub struct Provider {
    crypto: RustCrypto,
    storage: MemoryStorage,
}

impl OpenMlsProvider for Provider {
    type CryptoProvider = RustCrypto;
    type RandProvider = RustCrypto;
    type StorageProvider = MemoryStorage;
    fn storage(&self) -> &MemoryStorage { &self.storage }
    fn crypto(&self) -> &RustCrypto { &self.crypto }
    fn rand(&self) -> &RustCrypto { &self.crypto }
}

pub type MlsSnapshot = HashMap<Vec<u8>, Vec<u8>>;

impl Provider {
    pub fn snapshot(&self) -> MlsSnapshot { self.storage.values.read().expect("storage lock").clone() }
    pub fn restore(&self, v: MlsSnapshot) { *self.storage.values.write().expect("storage lock") = v; }
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceIdentity {
    #[serde(with = "hexser")] pub device_id: Id16,
    #[serde(with = "hexser")] pub member_id: Id16,
    #[serde(with = "hexser")] pub identity_sk: [u8; 32],
    #[serde(with = "hexser")] pub mls_public: Vec<u8>,
}

impl Drop for DeviceIdentity {
    fn drop(&mut self) { self.identity_sk.zeroize(); }
}

impl DeviceIdentity {
    pub fn identity_public(&self) -> [u8; 32] { util::ed25519_public(&self.identity_sk) }
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PendingJoin {
    /// `a` from the invite link: SHA-256 of the inviter's identityKey.
    #[serde(with = "hexser")] pub inviter_fingerprint: H32,
    pub head_version: u64,
    #[serde(with = "hexser")] pub head_hash: H32,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CountedHb {
    #[serde(with = "hexser")] pub signer_member: Id16,
    #[serde(with = "hexser")] pub signer_device: Id16,
    pub role: Role,
    pub roster_version: u64,
    pub time: u64,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TeamState {
    /// Persisted in its own `chain` row (written only when its fingerprint changes).
    #[serde(skip)]
    pub chain: Chain,
    pub join: Option<PendingJoin>,
    /// Lowest roster version a Welcome may be built on (invite `rosterHead`, R2-01).
    pub floor_version: u64,
    pub joined: bool,
    pub heartbeats: Vec<CountedHb>,
    /// N9: peers whose root key changed and whose safety number we have not verified yet.
    pub frozen_peers: BTreeSet<String>,
    pub removed: bool,
    /// Highest roster version seen claimed by a verified heartbeat.
    pub max_seen_version: u64,
    /// Invites created by this device (§3.4 п.4): `k_adm` never leaves it, keyed by hex(inviteId).
    #[serde(default)]
    pub invites: BTreeMap<String, InviteSecret>,
    /// SD-16/R2-07: the team's names (privatePart) as last received or set; see names.rs.
    #[serde(default)]
    pub names: Option<crate::names::PrivatePart>,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InviteSecret {
    #[serde(with = "hexser")] pub k_adm: H32,
    pub role: String,
    pub max_uses: u64,
    pub expires_at: u64,
    /// The device that created the invite (R2-05: received through the admin group; it must still be an admin).
    #[serde(default, with = "crate::util::hexopt")]
    pub inviter_device: Option<crate::util::Id16>,
}

impl Drop for InviteSecret {
    fn drop(&mut self) { self.k_adm.zeroize(); }
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GroupMeta {
    #[serde(with = "hexser")] pub group_id: H32,
    #[serde(with = "hexser")] pub team_id: H32,
    #[serde(with = "hexser")] pub descriptor: Vec<u8>,
    pub kind: GroupType,
    /// Roster version whose hash is in the current GroupContext.
    pub roster_version: u64,
    /// Older pair generation, superseded (N2a). No ops in or out.
    pub closed: bool,
    /// Our own leaf was removed.
    pub evicted: bool,
    /// Devices added from a last-resort KeyPackage, until their first UpdatePath (SD-11).
    pub weak_fs: BTreeSet<String>,
    pub track: GroupTrack,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct State {
    pub device: Option<DeviceIdentity>,
    pub teams: BTreeMap<String, TeamState>,
    pub groups: BTreeMap<String, GroupMeta>,
    /// Highest epoch ever seen per groupId (also for deleted/closed groups): old Welcome replay (SD-11).
    pub seen_epochs: BTreeMap<String, u64>,
    /// Teams present in state.db whose key has not been supplied yet (`team.unlock`).
    #[serde(skip)]
    pub locked: BTreeSet<String>,
}

impl State {
    pub fn device(&self) -> Res<&DeviceIdentity> { self.device.as_ref().ok_or_else(|| e(Code::InvalidInput, "device not initialised (device.init)")) }
    pub fn team(&self, id: &H32) -> Res<&TeamState> { self.teams.get(&hex(id)).ok_or_else(|| self.no_team(id)) }
    pub fn team_mut(&mut self, id: &H32) -> Res<&mut TeamState> {
        let x = self.no_team(id);
        self.teams.get_mut(&hex(id)).ok_or(x)
    }
    fn no_team(&self, id: &H32) -> Error {
        if self.locked.contains(&hex(id)) { e(Code::TeamLocked, format!("team {} is locked (team.unlock first)", hex(id))) }
        else { e(Code::NotFound, format!("unknown team {}", hex(id))) }
    }
    pub fn group(&self, id: &H32) -> Res<&GroupMeta> { self.groups.get(&hex(id)).ok_or_else(|| e(Code::NotFound, format!("unknown group {}", hex(id)))) }
    pub fn group_mut(&mut self, id: &H32) -> Res<&mut GroupMeta> { self.groups.get_mut(&hex(id)).ok_or_else(|| e(Code::NotFound, format!("unknown group {}", hex(id)))) }
}


/// Cheap change detector of a chain: every mutation (append, freeze, resolve, branch switch) changes one of these.
fn chain_fingerprint(c: &Chain) -> H32 {
    let last = c.versions.last().map(|v| util::sha256(&[&v.update])).unwrap_or([0; 32]);
    let n = |x: usize| (x as u64).to_be_bytes();
    util::sha256(&[&c.team_id, &n(c.versions.len()), &last, &n(c.certs.len()), &n(c.seen_members.len()), &n(c.seen_devices.len()),
        &[c.equivocation.is_some() as u8], &n(c.resolutions.len())])
}

fn mls_h(key: &[u8; 32], k: &[u8]) -> H32 { util::hmac256(key, &[b"ombra/mls-kv\0".as_slice(), k].concat()) }

fn mls_row(k: &[u8], v: &[u8]) -> Zeroizing<Vec<u8>> {
    let mut b = Zeroizing::new(Vec::with_capacity(4 + k.len() + v.len()));
    b.extend_from_slice(&(k.len() as u32).to_be_bytes());
    b.extend_from_slice(k);
    b.extend_from_slice(v);
    b
}

fn json<T: Serialize>(v: &T) -> Res<Zeroizing<Vec<u8>>> { serde_json::to_vec(v).map(Zeroizing::new).map_err(|x| e(Code::Internal, x.to_string())) }
fn from_json<T: serde::de::DeserializeOwned>(b: &[u8], what: &str) -> Res<T> { serde_json::from_slice(b).map_err(|x| e(Code::Storage, format!("{what} decode: {x}"))) }

pub struct Session {
    conn: Connection,
    key: Key,
    team_keys: RefCell<HashMap<H32, Key>>,
    pub state: State,
    pub provider: Provider,
    /// Row name → hash of the last persisted plaintext (chain rows: fingerprint).
    cache: HashMap<String, H32>,
}

impl Session {
    /// Open (or create) `state.db`. `key` comes from the OS key store. Teams start locked.
    pub fn open(path: &Path, key: [u8; 32], install_id: &[u8]) -> Res<Session> {
        let key = Zeroizing::new(key);
        ensure_ok(!install_id.is_empty() && install_id.len() <= 64, "installId must be 1..64 bytes")?;
        let conn = store::open_conn(path)?;
        let mut s = Session { conn, key, team_keys: RefCell::new(HashMap::new()), provider: Provider::default(), cache: HashMap::new(),
            state: State { device: None, teams: BTreeMap::new(), groups: BTreeMap::new(), seen_epochs: BTreeMap::new(), locked: BTreeSet::new() } };
        let db = Db { conn: &s.conn, dev_key: &s.key, team_keys: &s.team_keys };
        match db.meta_get("install") {
            Err(x) => return Err(e(Code::Storage, format!("state decryption failed (wrong key or tampered file): {}", x.msg))),
            Ok(None) => {
                s.conn.execute_batch("BEGIN IMMEDIATE").map_err(sq)?;
                let r = db.meta_put("install", install_id).and_then(|_| db.meta_put("format", FORMAT));
                s.conn.execute_batch(if r.is_ok() { "COMMIT" } else { "ROLLBACK" }).map_err(sq)?;
                r?;
                return Ok(s);
            }
            Ok(Some(i)) => {
                if !util::ct_eq(&i, install_id) { return err(Code::CloneDetected, "state belongs to another installation: re-pairing required"); }
            }
        }
        ensure_ok(db.meta_get("format")?.is_some_and(|f| f.as_slice() == FORMAT), "unsupported state format")?;
        if let Some(d) = db.meta_get("device")? {
            s.state.device = from_json(&d, "device")?;
            s.cache.insert("meta:device".into(), util::sha256(&[&d]));
        }
        if let Some(se) = db.meta_get("seen_epochs")? {
            s.state.seen_epochs = from_json(&se, "seen epochs")?;
            s.cache.insert("meta:seen_epochs".into(), util::sha256(&[&se]));
        }
        let mut map = HashMap::new();
        {
            let mut q = s.conn.prepare("SELECT h, v FROM mls_kv").map_err(sq)?;
            let rows = q.query_map([], |r| Ok((r.get::<_, Vec<u8>>(0)?, r.get::<_, Vec<u8>>(1)?))).map_err(sq)?;
            for row in rows {
                let (h, v) = row.map_err(sq)?;
                let pt = db.dev_open("mls_kv", &h, &v)?;
                let n = u32::from_be_bytes(pt.get(..4).ok_or_else(|| e(Code::Storage, "mls_kv row"))?.try_into().unwrap()) as usize;
                ensure_ok(pt.len() >= 4 + n, "mls_kv row")?;
                map.insert(pt[4..4 + n].to_vec(), pt[4 + n..].to_vec());
            }
        }
        s.provider.restore(map);
        let teams: Vec<Vec<u8>> = {
            let mut q = s.conn.prepare("SELECT team_id FROM team_lock").map_err(sq)?;
            let r = q.query_map([], |r| r.get(0)).map_err(sq)?.collect::<Result<_, _>>().map_err(sq)?;
            r
        };
        for t in teams { s.state.locked.insert(hex(&t)); }
        Ok(s)
    }

    /// Ids of the teams in state.db and whether their key was supplied.
    pub fn teams(&self) -> Vec<(String, bool)> {
        let mut v: Vec<(String, bool)> = self.state.locked.iter().map(|t| (t.clone(), false)).collect();
        v.extend(self.state.teams.keys().map(|t| (t.clone(), true)));
        v.sort();
        v
    }

    /// Supply a team's key (from the OS key store) and load its rows.
    pub fn unlock(&mut self, team: &H32, key: [u8; 32]) -> Res<()> {
        let key = Zeroizing::new(key);
        if self.team_keys.borrow().contains_key(team) { return Ok(()); }
        ensure!(store::check_team_key(&self.conn, team, &key)?, Code::InvalidInput, "wrong key for team {}", hex(team));
        self.team_keys.borrow_mut().insert(*team, key);
        let r = self.load_team(team);
        if r.is_err() { self.team_keys.borrow_mut().remove(team); }
        r
    }

    fn load_team(&mut self, team: &H32) -> Res<()> {
        let db = Db { conn: &self.conn, dev_key: &self.key, team_keys: &self.team_keys };
        let tk = hex(team);
        let row: Vec<u8> = self.conn.query_row("SELECT v FROM team WHERE team_id = ?1", [&team[..]], |r| r.get(0)).map_err(sq)?;
        let pt = db.team_open(team, "team", team, &row)?;
        let mut ts: TeamState = from_json(&pt, "team")?;
        let crow: Vec<u8> = self.conn.query_row("SELECT v FROM chain WHERE team_id = ?1", [&team[..]], |r| r.get(0)).map_err(sq)?;
        ts.chain = from_json(&db.team_open(team, "chain", team, &crow)?, "chain")?;
        ts.chain.rehydrate()?;
        let mut cache = vec![(format!("team:{tk}"), util::sha256(&[&pt])), (format!("chain:{tk}"), chain_fingerprint(&ts.chain))];
        let mut groups = vec![];
        {
            let mut q = self.conn.prepare("SELECT group_id, v FROM grp WHERE team_id = ?1").map_err(sq)?;
            let rows = q.query_map([&team[..]], |r| Ok((r.get::<_, Vec<u8>>(0)?, r.get::<_, Vec<u8>>(1)?))).map_err(sq)?;
            for row in rows {
                let (gid, v) = row.map_err(sq)?;
                let pt = db.team_open(team, "grp", &gid, &v)?;
                let g: GroupMeta = from_json(&pt, "group")?;
                cache.push((format!("grp:{}", hex(&gid)), util::sha256(&[&pt])));
                groups.push(g);
            }
        }
        self.state.teams.insert(tk.clone(), ts);
        for g in groups { self.state.groups.insert(hex(&g.group_id), g); }
        self.state.locked.remove(&tk);
        self.cache.extend(cache);
        Ok(())
    }

    /// Write every row whose content changed. Returns the cache updates to apply after COMMIT.
    fn flush(&self, db: &Db, mls_before: &MlsSnapshot) -> Res<Vec<(String, Option<H32>)>> {
        let mut upd = vec![];
        let changed = |name: &str, h: &H32| self.cache.get(name) != Some(h);
        if let Some(d) = &self.state.device {
            let pt = json(d)?;
            let h = util::sha256(&[&pt]);
            if changed("meta:device", &h) { db.meta_put("device", &pt)?; upd.push(("meta:device".to_string(), Some(h))); }
        }
        let pt = json(&self.state.seen_epochs)?;
        let h = util::sha256(&[&pt]);
        if changed("meta:seen_epochs", &h) { db.meta_put("seen_epochs", &pt)?; upd.push(("meta:seen_epochs".to_string(), Some(h))); }
        let mut live: BTreeSet<String> = BTreeSet::new();
        for (tk, t) in &self.state.teams {
            let tid = t.chain.team_id;
            let name = format!("team:{tk}");
            let pt = json(t)?;
            let h = util::sha256(&[&pt]);
            if changed(&name, &h) {
                db.conn.execute("INSERT OR REPLACE INTO team (team_id, v) VALUES (?1, ?2)", params![&tid[..], db.team_seal(&tid, "team", &tid, &pt)?]).map_err(sq)?;
                upd.push((name.clone(), Some(h)));
            }
            live.insert(name);
            let name = format!("chain:{tk}");
            let fp = chain_fingerprint(&t.chain);
            if changed(&name, &fp) {
                let pt = json(&t.chain)?;
                db.conn.execute("INSERT OR REPLACE INTO chain (team_id, v) VALUES (?1, ?2)", params![&tid[..], db.team_seal(&tid, "chain", &tid, &pt)?]).map_err(sq)?;
                upd.push((name.clone(), Some(fp)));
            }
            live.insert(name);
        }
        for (gk, g) in &self.state.groups {
            let name = format!("grp:{gk}");
            let pt = json(g)?;
            let h = util::sha256(&[&pt]);
            if changed(&name, &h) {
                db.conn.execute("INSERT OR REPLACE INTO grp (group_id, team_id, v) VALUES (?1, ?2, ?3)",
                    params![&g.group_id[..], &g.team_id[..], db.team_seal(&g.team_id, "grp", &g.group_id, &pt)?]).map_err(sq)?;
                upd.push((name.clone(), Some(h)));
            }
            live.insert(name);
        }
        for name in self.cache.keys().filter(|n| !n.starts_with("meta:") && !live.contains(*n)) {
            let (kind, id) = name.split_once(':').expect("cache key");
            let id = util::unhex(id)?;
            let sql = match kind { "team" => "DELETE FROM team WHERE team_id = ?1", "chain" => "DELETE FROM chain WHERE team_id = ?1", _ => "DELETE FROM grp WHERE group_id = ?1" };
            db.conn.execute(sql, [&id]).map_err(sq)?;
            upd.push((name.clone(), None));
        }
        let now = self.provider.storage.values.read().expect("storage lock");
        for (k, v) in now.iter() {
            if mls_before.get(k) != Some(v) {
                let h = mls_h(&self.key, k);
                db.conn.execute("INSERT OR REPLACE INTO mls_kv (h, v) VALUES (?1, ?2)", params![&h[..], db.dev_seal("mls_kv", &h, &mls_row(k, v))]).map_err(sq)?;
            }
        }
        for k in mls_before.keys().filter(|k| !now.contains_key(*k)) {
            db.conn.execute("DELETE FROM mls_kv WHERE h = ?1", [&mls_h(&self.key, k)[..]]).map_err(sq)?;
        }
        Ok(upd)
    }

    /// Replace the state key and re-encrypt the device rows under it (one transaction). On failure the old key stays.
    pub fn rekey(&mut self, key: [u8; 32]) -> Res<()> {
        let new = Zeroizing::new(key);
        self.conn.execute_batch("BEGIN IMMEDIATE").map_err(sq)?;
        let r = (|| -> Res<()> {
            let old = Db { conn: &self.conn, dev_key: &self.key, team_keys: &self.team_keys };
            let fresh = Db { conn: &self.conn, dev_key: &new, team_keys: &self.team_keys };
            let metas: Vec<(String, Vec<u8>)> = {
                let mut q = self.conn.prepare("SELECT name, v FROM meta").map_err(sq)?;
                let r = q.query_map([], |r| Ok((r.get(0)?, r.get(1)?))).map_err(sq)?.collect::<Result<_, _>>().map_err(sq)?;
                r
            };
            for (n, v) in metas { let pt = old.dev_open("meta", n.as_bytes(), &v)?; fresh.meta_put(&n, &pt)?; }
            self.conn.execute("DELETE FROM mls_kv", []).map_err(sq)?;
            for (k, v) in self.provider.storage.values.read().expect("storage lock").iter() {
                let h = mls_h(&new, k);
                self.conn.execute("INSERT INTO mls_kv (h, v) VALUES (?1, ?2)", params![&h[..], fresh.dev_seal("mls_kv", &h, &mls_row(k, v))]).map_err(sq)?;
            }
            let logs: Vec<(i64, Vec<u8>)> = {
                let mut q = self.conn.prepare("SELECT id, v FROM seclog").map_err(sq)?;
                let r = q.query_map([], |r| Ok((r.get(0)?, r.get(1)?))).map_err(sq)?.collect::<Result<_, _>>().map_err(sq)?;
                r
            };
            for (id, v) in logs {
                let pt = old.dev_open("seclog", b"", &v)?;
                self.conn.execute("UPDATE seclog SET v = ?1 WHERE id = ?2", params![fresh.dev_seal("seclog", b"", &pt), id]).map_err(sq)?;
            }
            Ok(())
        })();
        match r {
            Ok(()) => {
                self.conn.execute_batch("COMMIT").map_err(sq)?;
                self.key = new;
                self.checkpoint();
                Ok(())
            }
            Err(x) => { let _ = self.conn.execute_batch("ROLLBACK"); Err(x) }
        }
    }

    /// Move the WAL into the main file and truncate it (after a wipe: no old pages of the team linger in the WAL).
    pub fn checkpoint(&self) { let _ = self.conn.execute_batch("PRAGMA wal_checkpoint(TRUNCATE)"); }

    /// Run `f` atomically (see module docs).
    pub fn transact<T>(&mut self, f: impl FnOnce(&mut State, &Provider, &Db) -> Res<T>) -> Res<T> {
        self.conn.execute_batch("BEGIN IMMEDIATE").map_err(sq)?;
        let state_before = self.state.clone();
        let mls_before = self.provider.snapshot();
        let keys_before = self.team_keys.borrow().clone();
        let out = {
            let db = Db { conn: &self.conn, dev_key: &self.key, team_keys: &self.team_keys };
            f(&mut self.state, &self.provider, &db)
        };
        let persist = match &out { Ok(_) => true, Err(Error { code: Code::Equivocation, .. }) => true, Err(_) => false };
        if persist {
            let db = Db { conn: &self.conn, dev_key: &self.key, team_keys: &self.team_keys };
            let committed = self.flush(&db, &mls_before).and_then(|upd| {
                store::failpoint("before-commit");
                self.conn.execute_batch("COMMIT").map_err(sq)?;
                store::failpoint("after-commit");
                Ok(upd)
            });
            match committed {
                Ok(upd) => {
                    for (k, h) in upd { match h { Some(h) => { self.cache.insert(k, h); } None => { self.cache.remove(&k); } } }
                    return out;
                }
                Err(x) => {
                    let _ = self.conn.execute_batch("ROLLBACK");
                    self.state = state_before;
                    self.provider.restore(mls_before);
                    *self.team_keys.borrow_mut() = keys_before;
                    return Err(x);
                }
            }
        }
        let _ = self.conn.execute_batch("ROLLBACK");
        self.state = state_before;
        self.provider.restore(mls_before);
        *self.team_keys.borrow_mut() = keys_before;
        out
    }
}

fn ensure_ok(c: bool, msg: &str) -> Res<()> { if c { Ok(()) } else { err(Code::InvalidInput, msg) } }
