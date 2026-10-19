//! Item-op rules (§3.0 operation table, ред. 10 kinds/statuses, R5-03/04/05/08/10/12, N3/N4/N5) and the local replica
//! they build, stored row by row in `state.db` (`item`, `status`, `share`, `xfer` tables under the team key; never
//! loaded as a whole). Everything here is order-independent of the server: items follow the author's `rev`, statuses
//! follow the recipient's `seq`, gaps in `seq` are surfaced.
//!
//! Invariants:
//! - an op is applied only after `verify_live` (MLS sender = signerDeviceId, epoch = delivery
//!   epoch, descriptor hash = this group, rosterVersion = the version whose hash is in this epoch's
//!   GroupContext, signer active and not compromised (R2-10) in that version, signature valid) or,
//!   for share archives, `verify_at_version` (signature + chain at the op's own `rosterVersion`);
//! - `(authorMemberId, itemId)` is the item key; a second create for a known key is rejected;
//!   edit/collection ops only by the author, delete by the author or (space items) an admin when
//!   the author is neither owner nor admin; ops for unknown keys are ignored, never create items;
//! - the same `rev` from two devices of the author: the smaller `(seq, signerDeviceId)` wins on every replica, in
//!   any delivery order; a device whose own edit lost keeps a copy of it (`replaced`) for the UI;
//! - status only inside the author–recipient pair group, only about the signer itself, allowed for the item kind:
//!   marks `seen|done|dismissed`, resources `seen|accepted|declined`. `seen` once, only before the first final status;
//!   a resource's final status is the first one (min `(seq, device)`) and never changes; a mark's is the last (max);
//! - `transfer.key` only in a pair (admin → the peer's active device) or the own personal group (own device);
//! - a tombstone erases content, attachment keys and the original signed ops, keeping only
//!   `(itemKey, rev, tombstone op)`; pending buffered ops for the key are dropped;
//! - replays (same opId, or same `(signerDeviceId, seq)`) are rejected by the delivery receipts (`store::Db::seen_*`).

use std::collections::BTreeMap;

use rusqlite::params;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value as J};

use crate::cbor::{self, Fields, MapB, Value};
use crate::descriptor::{Descriptor, GroupType};
use crate::ensure;
use crate::itemop::{self, Audience, Content, ItemKey, Kind, Payload, SignedOp, StatusKind, ZERO16};
use crate::roster::{Chain, Projection, Role};
use crate::store::{sq, Db};
use crate::util::{self, e, err, hex, hexopt, hexser, hexvec, Code, Id16, Res, H32};

// ---------------- seq tracking (R5-08, N5) ----------------

/// Sorted, disjoint, inclusive ranges.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct RangeSet(pub Vec<(u64, u64)>);

impl RangeSet {
    pub fn insert(&mut self, a: u64, b: u64) {
        let mut v: Vec<(u64, u64)> = Vec::with_capacity(self.0.len() + 1);
        let (mut a, mut b) = (a, b);
        for &(x, y) in &self.0 {
            if y.saturating_add(1) < a || b.saturating_add(1) < x { v.push((x, y)); } else { a = a.min(x); b = b.max(y); }
        }
        v.push((a, b));
        v.sort();
        self.0 = v;
    }
    pub fn contains(&self, x: u64) -> bool { self.0.iter().any(|&(a, b)| a <= x && x <= b) }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SeqTrack {
    /// First seq seen from this member after we joined (N5 base): no gap is reported below it.
    pub base: Option<u64>,
    pub received: RangeSet,
    pub skipped: RangeSet,
    /// Highest seq known to exist (ops, header seqHead, seq.head beacons).
    pub max_known: u64,
    /// `(first seq of a newly claimed range, local time it was claimed)`; pruned below the first open gap.
    pub marks: Vec<(u64, u64)>,
    pub last_heard: u64,
    pub last_op: u64,
}

impl SeqTrack {
    fn claim(&mut self, upto: u64, now: u64) {
        if upto > self.max_known {
            self.marks.push((self.max_known + 1, now));
            self.max_known = upto;
        }
    }

    fn covered(&self, x: u64) -> bool { self.received.contains(x) || self.skipped.contains(x) }

    /// Keep marks bounded: only the last mark at or below the first missing seq is ever consulted for it.
    fn prune(&mut self) {
        let Some(base) = self.base else { return };
        let mut first = base;
        while first <= self.max_known && self.covered(first) {
            first = self.received.0.iter().chain(self.skipped.0.iter()).filter(|&&(a, b)| a <= first && first <= b).map(|&(_, b)| b + 1).max().unwrap_or(first + 1);
        }
        if let Some(keep) = self.marks.iter().rposition(|(s, _)| *s <= first) { self.marks.drain(..keep); }
    }

    /// Missing ranges `[from, to]` with the local time since which they are missing.
    pub fn gaps(&self) -> Vec<(u64, u64, u64)> {
        let Some(base) = self.base else { return vec![] };
        let mut out = vec![];
        let mut x = base;
        while x <= self.max_known {
            if self.covered(x) { x += 1; continue; }
            let start = x;
            while x <= self.max_known && !self.covered(x) { x += 1; }
            let since = self.marks.iter().filter(|(s, _)| *s <= start).map(|(_, t)| *t).last().unwrap_or(0);
            out.push((start, x - 1, since));
            if out.len() >= 64 { break; }
        }
        out
    }
}

/// Per-group sequence tracks by hex(memberId). Replay protection lives in the delivery receipts, not here.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GroupTrack {
    pub seq: BTreeMap<String, SeqTrack>,
}

impl GroupTrack {
    pub fn record(&mut self, op: &SignedOp, payload: &Payload, chain_member_of: impl Fn(&Id16) -> bool, now: u64) {
        let h = &op.header;
        let t = self.seq.entry(hex(&h.signer_member_id)).or_default();
        t.last_heard = now;
        match payload {
            Payload::SeqHead { last_seq } => {
                if t.base.is_none() { t.base = Some(last_seq + 1); t.max_known = *last_seq; } else { t.claim(*last_seq, now); }
            }
            Payload::SeqSkip { from, to, device } => {
                if chain_member_of(device) { t.skipped.insert(*from, *to); t.claim(*to, now); }
            }
            _ => {
                t.last_op = now;
                if t.base.is_none() { t.base = Some(h.seq); t.max_known = h.seq.saturating_sub(1); }
                t.received.insert(h.seq, h.seq);
                t.claim(h.seq.max(h.seq_head), now);
            }
        }
        t.prune();
    }
}

// ---------------- replica records ----------------

/// The op that produced the current `rev` (edit/collection), with the content before it: lets a smaller
/// same-rev op from another device of the author take its place (multi-device auto-winner).
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RevHead {
    pub seq: u64,
    #[serde(with = "hexser")] pub device: Id16,
    #[serde(with = "hexser")] pub prev_content: Vec<u8>,
}

/// This device's own edit that lost to the same rev from another device of the author (banner + copy of the text).
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Replaced {
    pub rev: u64,
    #[serde(with = "hexser")] pub content: Vec<u8>,
    #[serde(with = "hexser")] pub by_device: Id16,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ItemRec {
    #[serde(with = "hexser")] pub author: Id16,
    #[serde(with = "hexser")] pub item_id: Id16,
    pub kind: String,
    #[serde(default, with = "hexopt")] pub space: Option<Vec<u8>>,
    #[serde(default, with = "hexopt")] pub recipient: Option<Id16>,
    pub home: String,
    pub rev: u64,
    pub created_at: u64,
    /// Canonical CBOR of the current content map; empty after a tombstone.
    #[serde(with = "hexser")] pub content: Vec<u8>,
    /// Original signed ops (create, edits, collection ops); erased by a tombstone.
    #[serde(with = "hexvec")] pub ops: Vec<Vec<u8>>,
    #[serde(default, with = "hexopt")] pub tombstone: Option<Vec<u8>>,
    #[serde(with = "hexvec")] pub pending: Vec<Vec<u8>>,
    #[serde(default)] pub head: Option<RevHead>,
    #[serde(default)] pub replaced: Option<Replaced>,
}

impl ItemRec {
    pub fn key(&self) -> ItemKey { ItemKey { author: self.author, item_id: self.item_id } }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Stamp {
    pub seq: u64,
    #[serde(with = "hexser")] pub device: Id16,
}

impl Stamp {
    fn k(&self) -> (u64, Id16) { (self.seq, self.device) }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FinalRec {
    pub status: String,
    pub note: Option<String>,
    pub at: Stamp,
}

/// One recipient's statuses of one item (§3.0 ред. 10).
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StatusRec {
    pub seen: Option<Stamp>,
    /// Current final status: resources — the first (min stamp), marks — the last (max stamp).
    pub fin: Option<FinalRec>,
    /// Smallest stamp of any final status ever applied: `seen` is valid only below it.
    pub first_final: Option<Stamp>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShareRec {
    #[serde(with = "hexser")] pub signer_member: Id16,
    #[serde(with = "hexser")] pub signer_device: Id16,
    #[serde(with = "hexser")] pub target_member: Id16,
    #[serde(with = "hexser")] pub target_device: Id16,
    #[serde(with = "hexvec")] pub scopes: Vec<Vec<u8>>,
    pub count: u64,
    #[serde(with = "hexser")] pub sha256: H32,
    pub group_kind: GroupType,
    #[serde(default)] pub consumed: bool,
}

/// `transfer.key` (§3.3): the key and parameters of a `/v1/transfers` upload.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct XferRec {
    #[serde(with = "hexser")] pub transfer_id: Id16,
    #[serde(with = "hexser")] pub signer_member: Id16,
    #[serde(with = "hexser")] pub signer_device: Id16,
    #[serde(with = "hexser")] pub target_member: Id16,
    #[serde(with = "hexser")] pub target_device: Id16,
    pub size: u64,
    #[serde(with = "hexser")] pub sha256: H32,
    #[serde(with = "hexser")] pub key: H32,
}

pub fn home_of(d: &Descriptor) -> String {
    match d.kind {
        GroupType::Team => format!("s:{}", hex(&d.team_id)),
        GroupType::Project => format!("s:{}", hex(&d.project_id.unwrap())),
        GroupType::Pair => format!("p:{}{}", hex(&d.members[0]), hex(&d.members[1])),
        GroupType::Personal => format!("o:{}", hex(&d.owner.unwrap())),
        GroupType::Admin => format!("a:{}", hex(&d.team_id)),
    }
}

/// Home of the pair of two members (independent of the generation).
pub fn pair_home(a: &Id16, b: &Id16) -> String {
    let (x, y) = if a <= b { (a, b) } else { (b, a) };
    format!("p:{}{}", hex(x), hex(y))
}

#[derive(Debug, PartialEq, Eq)]
pub enum Outcome { Applied, Buffered, Ignored(String) }

impl Outcome {
    pub fn json(&self) -> J {
        match self { Outcome::Applied => json!("applied"), Outcome::Buffered => json!("buffered"), Outcome::Ignored(w) => json!({"ignored": w}) }
    }
}

/// Live-delivery context of one MLS application message.
pub struct LiveCtx<'a> {
    pub chain: &'a Chain,
    pub desc: &'a Descriptor,
    pub group_id: &'a H32,
    pub epoch: u64,
    /// Roster version whose hash is in this epoch's GroupContext.
    pub group_version: u64,
    pub mls_sender: &'a Id16,
}

/// Checks that bind the op to its live delivery (R5-03, N4).
pub fn verify_live<'a>(ctx: &LiveCtx<'a>, op: &SignedOp) -> Res<&'a Projection> {
    let h = &op.header;
    ensure!(h.team_id == ctx.chain.team_id && ctx.desc.team_id == ctx.chain.team_id, Code::OpRejected, "op for another team");
    ensure!(&h.group_descriptor_hash == ctx.group_id, Code::OpRejected, "groupDescriptorHash != this group");
    ensure!(h.epoch == ctx.epoch, Code::OpRejected, "signed epoch {} != delivery epoch {}", h.epoch, ctx.epoch);
    ensure!(&h.signer_device_id == ctx.mls_sender, Code::OpRejected, "MLS sender != signerDeviceId");
    ensure!(h.roster_version == ctx.group_version, Code::OpRejected,
        "rosterVersion {} != roster version {} of this epoch (N4)", h.roster_version, ctx.group_version);
    verify_at_version(ctx.chain, op)
}

/// Signature + chain checks at the op's own `rosterVersion` (also used for share archives).
pub fn verify_at_version<'a>(chain: &'a Chain, op: &SignedOp) -> Res<&'a Projection> {
    let h = &op.header;
    ensure!(h.team_id == chain.team_id, Code::OpRejected, "op for another team");
    let p = chain.version(h.roster_version).ok_or_else(|| e(Code::NeedRoster, format!("roster v{} unknown", h.roster_version)))?;
    ensure!(chain.hash_of(h.roster_version) == Some(h.roster_hash), Code::OpRejected, "rosterHash != chain hash of rosterVersion");
    let (m, _) = p.active_device(&h.signer_device_id)
        .ok_or_else(|| e(Code::OpRejected, format!("signer device not active in roster v{} (revoked or cascaded)", h.roster_version)))?;
    ensure!(m.member_id == h.signer_member_id, Code::OpRejected, "signerMemberId does not own signerDeviceId");
    ensure!(!chain.compromised_at(&h.signer_device_id, h.roster_version), Code::OpRejected,
        "signer device compromised at roster v{} (revoked with compromisedSince <= that version, or cascaded, R2-10)", h.roster_version);
    let cert = chain.cert(&h.signer_device_id).ok_or_else(|| e(Code::OpRejected, "no certificate for signer"))?;
    ensure!(op.verify_sig(&cert.identity_key), Code::BadSignature, "item-op signature invalid");
    Ok(p)
}

pub fn space_access(p: &Projection, team_id: &H32, scope: &[u8], member: &Id16, write: bool) -> bool {
    if p.member(member).is_none() { return false; }
    if scope == team_id.as_slice() { return true; }
    let Ok(pid) = <Id16>::try_from(scope) else { return false };
    match p.access_level(&pid, member) {
        Some(crate::roster::Level::Contributor) => true,
        Some(crate::roster::Level::Viewer) => !write,
        None => false,
    }
}

/// Permission checks that depend on the roster version an op is signed in (re-run when an outbox op is sealed
/// under a newer epoch): space write access for creates, admin rights for a non-author delete.
pub fn check_signed_rights(chain: &Chain, at: &Projection, op: &SignedOp) -> Res<()> {
    let h = &op.header;
    let ty = op.op_type()?;
    match Payload::parse(ty, &op.payload)? {
        Payload::Create { audience: Audience::Space(scope), .. } =>
            ensure!(space_access(at, &chain.team_id, &scope, &h.signer_member_id, true), Code::OpRejected, "author has no write access to scope"),
        Payload::Delete if h.signer_member_id != h.author_member_id =>
            ensure!(at.role(&h.signer_member_id).is_some_and(Role::is_admin) && !at.role(&h.author_member_id).is_some_and(Role::is_admin),
                Code::OpRejected, "delete only by the author, or an admin for space items of non-admin authors"),
        Payload::TransferKey { .. } if h.recipient_member_id != h.signer_member_id =>
            ensure!(at.role(&h.signer_member_id).is_some_and(Role::is_admin), Code::OpRejected, "transfer.key to another member only by an admin"),
        _ => {}
    }
    Ok(())
}

fn content_map(c: &Content) -> Vec<u8> { cbor::encode(&itemop::content_value(c, MapB::new()).build()) }

pub fn content_from(bytes: &[u8]) -> Res<Content> {
    if bytes.is_empty() { return Ok(Content::default()); }
    let mut f = Fields::new(cbor::decode(bytes)?, "content")?;
    let c = Content { anchor: f.opt("anchor"), context: f.opt("context"), body: f.opt("body"), attachments: f.opt("attachments") };
    f.finish()?;
    Ok(c)
}

pub struct ApplyCtx<'a> {
    pub chain: &'a Chain,
    /// Roster version the op was signed in (already verified).
    pub at: &'a Projection,
    /// The receiver's current (head) roster for "current roster" rules (status access, N3).
    pub head: &'a Projection,
    /// Home of the group the op arrived in (`None` for archive ops: home follows the item scope).
    pub home: Option<String>,
    pub group_kind: Option<GroupType>,
    pub desc: Option<&'a Descriptor>,
    /// This device (marks own edits replaced by another device's same-rev edit).
    pub me: Option<Id16>,
}

fn ikey(k: &ItemKey) -> [u8; 32] {
    let mut b = [0u8; 32];
    b[..16].copy_from_slice(&k.author);
    b[16..].copy_from_slice(&k.item_id);
    b
}

fn dec<T: serde::de::DeserializeOwned>(b: &[u8], what: &str) -> Res<T> { serde_json::from_slice(b).map_err(|x| e(Code::Storage, format!("{what}: {x}"))) }
fn enc<T: Serialize>(v: &T) -> Res<zeroize::Zeroizing<Vec<u8>>> { serde_json::to_vec(v).map(zeroize::Zeroizing::new).map_err(|x| e(Code::Internal, x.to_string())) }

/// The replica of one team inside the current transaction.
pub struct Replica<'a, 'b> {
    pub db: &'a Db<'b>,
    pub team: H32,
}

impl<'a, 'b> Replica<'a, 'b> {
    pub fn new(db: &'a Db<'b>, team: H32) -> Replica<'a, 'b> { Replica { db, team } }

    pub fn item(&self, k: &ItemKey) -> Res<Option<ItemRec>> {
        self.db.trow_get("item", "ikey", &self.team, &ikey(k))?.map(|b| dec(&b, "item")).transpose()
    }
    pub fn put_item(&self, it: &ItemRec) -> Res<()> {
        let k = ikey(&it.key());
        let pk = [&self.team[..], &k[..]].concat();
        let v = self.db.team_seal(&self.team, "item", &pk, &enc(it)?)?;
        self.db.conn.execute("INSERT OR REPLACE INTO item (team_id, ikey, created_at, v) VALUES (?1, ?2, ?3, ?4)",
            params![&self.team[..], &k[..], it.created_at as i64, v]).map_err(sq)?;
        Ok(())
    }
    /// Items newest first (`created_at` desc), at most `limit`, optionally strictly older than `before`.
    pub fn items(&self, limit: u64, before: Option<u64>) -> Res<Vec<ItemRec>> {
        let mut q = self.db.conn.prepare("SELECT ikey, v FROM item WHERE team_id = ?1 AND created_at < ?2 ORDER BY created_at DESC, ikey LIMIT ?3").map_err(sq)?;
        let rows = q.query_map(params![&self.team[..], before.map(|b| b as i64).unwrap_or(i64::MAX), limit.min(i64::MAX as u64) as i64],
            |r| Ok((r.get::<_, Vec<u8>>(0)?, r.get::<_, Vec<u8>>(1)?))).map_err(sq)?;
        let mut out = vec![];
        for row in rows {
            let (k, v) = row.map_err(sq)?;
            out.push(dec(&self.db.team_open(&self.team, "item", &[&self.team[..], &k[..]].concat(), &v)?, "item")?);
        }
        Ok(out)
    }

    pub fn status(&self, k: &ItemKey, recipient: &Id16) -> Res<StatusRec> {
        let pk = [&self.team[..], &ikey(k)[..], &recipient[..]].concat();
        let v: Option<Vec<u8>> = self.db.conn.query_row("SELECT v FROM status WHERE team_id = ?1 AND ikey = ?2 AND recipient = ?3",
            params![&self.team[..], &ikey(k)[..], &recipient[..]], |r| r.get(0)).optional_row()?;
        v.map(|v| self.db.team_open(&self.team, "status", &pk, &v).and_then(|b| dec(&b, "status"))).transpose().map(Option::unwrap_or_default)
    }
    pub fn put_status(&self, k: &ItemKey, recipient: &Id16, s: &StatusRec) -> Res<()> {
        let pk = [&self.team[..], &ikey(k)[..], &recipient[..]].concat();
        let v = self.db.team_seal(&self.team, "status", &pk, &enc(s)?)?;
        self.db.conn.execute("INSERT OR REPLACE INTO status (team_id, ikey, recipient, v) VALUES (?1, ?2, ?3, ?4)",
            params![&self.team[..], &ikey(k)[..], &recipient[..], v]).map_err(sq)?;
        Ok(())
    }
    pub fn statuses(&self, k: &ItemKey) -> Res<Vec<(Id16, StatusRec)>> {
        let mut q = self.db.conn.prepare("SELECT recipient, v FROM status WHERE team_id = ?1 AND ikey = ?2 ORDER BY recipient").map_err(sq)?;
        let rows = q.query_map(params![&self.team[..], &ikey(k)[..]], |r| Ok((r.get::<_, Vec<u8>>(0)?, r.get::<_, Vec<u8>>(1)?))).map_err(sq)?;
        let mut out = vec![];
        for row in rows {
            let (r, v) = row.map_err(sq)?;
            let pk = [&self.team[..], &ikey(k)[..], &r[..]].concat();
            out.push((util::id16(&r)?, dec(&self.db.team_open(&self.team, "status", &pk, &v)?, "status")?));
        }
        Ok(out)
    }
    fn delete_statuses(&self, k: &ItemKey) -> Res<()> {
        self.db.conn.execute("DELETE FROM status WHERE team_id = ?1 AND ikey = ?2", params![&self.team[..], &ikey(k)[..]]).map_err(sq)?;
        Ok(())
    }

    pub fn share(&self, id: &Id16) -> Res<Option<ShareRec>> { self.db.trow_get("share", "share_id", &self.team, id)?.map(|b| dec(&b, "share")).transpose() }
    pub fn put_share(&self, id: &Id16, s: &ShareRec) -> Res<()> { self.db.trow_put("share", "share_id", &self.team, id, &enc(s)?) }
    pub fn transfer(&self, id: &Id16) -> Res<Option<XferRec>> { self.db.trow_get("xfer", "transfer_id", &self.team, id)?.map(|b| dec(&b, "transfer")).transpose() }

    /// Apply a verified op. Errors leave the replica untouched (callers run inside a transaction / savepoint).
    pub fn apply(&self, cx: &ApplyCtx, op: &SignedOp, raw: &[u8]) -> Res<Outcome> {
        let ty = op.op_type()?;
        let payload = Payload::parse(ty, &op.payload).map_err(|x| e(Code::OpRejected, x.msg))?;
        let h = &op.header;
        let signer = h.signer_member_id;
        ensure!(ty.is_control() == (h.seq == 0), Code::OpRejected, "seq must be 0 exactly for seq.head/seq.skip");
        let key = op.item_key();
        match &payload {
            Payload::Create { kind, audience, created_at, content } => {
                ensure!(h.author_member_id == signer, Code::OpRejected, "create must be signed by its author");
                ensure!(h.rev == 1, Code::OpRejected, "create must have rev 1");
                let (space, recipient, home) = match audience {
                    Audience::Space(scope) => {
                        ensure!(h.recipient_member_id == ZERO16, Code::OpRejected, "space item with recipient");
                        let home = format!("s:{}", hex(scope));
                        if let Some(gh) = &cx.home { ensure!(*gh == home, Code::OpRejected, "space item scope != this group's scope"); }
                        ensure!(space_access(cx.at, &cx.chain.team_id, scope, &signer, true), Code::OpRejected, "author has no write access to scope");
                        (Some(scope.clone()), None, home)
                    }
                    Audience::Member(r) => {
                        let d = cx.desc.ok_or_else(|| e(Code::OpRejected, "member item outside a pair group"))?;
                        ensure!(d.kind == GroupType::Pair, Code::OpRejected, "member item must travel in the author–recipient pair");
                        ensure!(d.peer_of(&signer) == Some(*r) && h.recipient_member_id == *r, Code::OpRejected, "recipient must be the other pair member");
                        (None, Some(*r), home_of(d))
                    }
                };
                ensure!(self.item(&key)?.is_none(), Code::OpRejected, "create with an already known key (R5-04)");
                self.put_item(&ItemRec {
                    author: key.author, item_id: key.item_id, kind: kind.as_str().into(), space, recipient, home,
                    rev: 1, created_at: *created_at, content: content_map(content), ops: vec![raw.to_vec()], tombstone: None, pending: vec![],
                    head: None, replaced: None,
                })?;
                Ok(Outcome::Applied)
            }
            Payload::Edit { .. } | Payload::CollectionAdd(_) | Payload::CollectionRemove(_) => {
                let Some(mut it) = self.item(&key)? else { return Ok(Outcome::Ignored("unknown item key".into())) };
                if it.tombstone.is_some() { return Ok(Outcome::Ignored("item deleted".into())); }
                ensure!(h.author_member_id == signer && it.author == signer, Code::OpRejected, "{} only by the author", ty.name());
                if let Some(gh) = &cx.home { ensure!(*gh == it.home, Code::OpRejected, "op outside the item's group"); }
                if !matches!(payload, Payload::Edit { .. }) {
                    ensure!(it.kind == "collection", Code::OpRejected, "collection op on a non-collection");
                }
                if let Payload::Edit { base_rev, .. } = &payload { ensure!(h.rev == base_rev + 1, Code::OpRejected, "edit rev must be baseRev + 1"); }
                if h.rev == it.rev {
                    if let Some(hd) = it.head.clone() {
                        // Same rev from two devices of the author: the smaller (seq, signerDeviceId) wins everywhere.
                        let inc = (h.seq, h.signer_device_id);
                        if inc == (hd.seq, hd.device) { return Ok(Outcome::Ignored("same rev op already applied".into())); }
                        if inc > (hd.seq, hd.device) { return Ok(Outcome::Ignored("lost to the same rev from another device of the author".into())); }
                        let loser = std::mem::replace(&mut it.content, hd.prev_content.clone());
                        it.rev -= 1;
                        it.ops.pop();
                        if cx.me == Some(hd.device) { it.replaced = Some(Replaced { rev: h.rev, content: loser, by_device: h.signer_device_id }); }
                        apply_rev_op(&mut it, &payload, raw, h.seq, h.signer_device_id)?;
                        self.drain_pending(&mut it)?;
                        self.put_item(&it)?;
                        return Ok(Outcome::Applied);
                    }
                }
                if h.rev <= it.rev { return err(Code::OpRejected, format!("stale rev {} (current {})", h.rev, it.rev)); }
                if h.rev > it.rev + 1 {
                    ensure!(it.pending.len() < 256, Code::OpRejected, "too many buffered ops");
                    it.pending.push(raw.to_vec());
                    self.put_item(&it)?;
                    return Ok(Outcome::Buffered);
                }
                apply_rev_op(&mut it, &payload, raw, h.seq, h.signer_device_id)?;
                self.drain_pending(&mut it)?;
                self.put_item(&it)?;
                Ok(Outcome::Applied)
            }
            Payload::Delete => {
                let Some(mut it) = self.item(&key)? else { return Ok(Outcome::Ignored("unknown item key".into())) };
                if it.tombstone.is_some() { return Ok(Outcome::Ignored("already deleted".into())); }
                ensure!(h.author_member_id == it.author, Code::OpRejected, "authorMemberId != item author");
                if let Some(gh) = &cx.home { ensure!(*gh == it.home, Code::OpRejected, "delete outside the item's group"); }
                let by_author = signer == it.author;
                let by_admin = it.space.is_some() && cx.at.role(&signer).is_some_and(Role::is_admin)
                    && !cx.at.role(&it.author).is_some_and(Role::is_admin);
                ensure!(by_author || by_admin, Code::OpRejected, "delete only by the author, or an admin for space items of non-admin authors");
                // R5-05: erase payload, context, attachment keys and the original signed ops.
                it.content = vec![];
                it.ops.clear();
                it.pending.clear();
                it.head = None;
                it.replaced = None;
                it.rev = it.rev.max(h.rev);
                it.tombstone = Some(raw.to_vec());
                self.put_item(&it)?;
                self.delete_statuses(&key)?;
                Ok(Outcome::Applied)
            }
            Payload::Status { status, note } => {
                let d = cx.desc.ok_or_else(|| e(Code::OpRejected, "status outside a pair group"))?;
                ensure!(d.kind == GroupType::Pair, Code::OpRejected, "status only in the author–recipient pair, never in a scope group");
                ensure!(h.recipient_member_id == signer, Code::OpRejected, "status only about the signer itself");
                ensure!(d.peer_of(&signer) == Some(h.author_member_id), Code::OpRejected, "status must travel in the pair of item author and recipient");
                let Some(it) = self.item(&key)? else { return Ok(Outcome::Ignored("unknown item key".into())) };
                if it.tombstone.is_some() { return Ok(Outcome::Ignored("item deleted".into())); }
                ensure!(it.author != signer, Code::OpRejected, "author cannot post a recipient status");
                match (&it.recipient, &it.space) {
                    (Some(r), _) => ensure!(*r == signer, Code::OpRejected, "status from a non-recipient"),
                    (None, Some(scope)) => ensure!(space_access(cx.head, &cx.chain.team_id, scope, &signer, false), Code::OpRejected,
                        "status for a space item from a member without access in the current roster"),
                    _ => return err(Code::OpRejected, "item has no audience"),
                }
                let kind = Kind::parse(&it.kind)?;
                ensure!(status.allowed_for(kind), Code::OpRejected, "status '{}' is not allowed for a {}", status.as_str(), kind.as_str());
                let mut rec = self.status(&key, &signer)?;
                let me = Stamp { seq: h.seq, device: h.signer_device_id };
                let outcome = transition(&mut rec, *status, note.clone(), me, kind.is_resource())?;
                if outcome == Outcome::Applied { self.put_status(&key, &signer, &rec)?; }
                Ok(outcome)
            }
            Payload::Share { share_id, target_member, target_device, scopes, count, sha256 } => {
                let kind = cx.group_kind.ok_or_else(|| e(Code::OpRejected, "share outside a group"))?;
                match kind {
                    GroupType::Team => ensure!(cx.at.role(&signer).is_some_and(Role::is_admin), Code::OpRejected, "share in the team group only by an admin"),
                    GroupType::Personal => ensure!(cx.desc.and_then(|d| d.owner) == Some(signer) && *target_member == signer,
                        Code::OpRejected, "share in a personal group only by its owner to itself"),
                    _ => return err(Code::OpRejected, "share is not allowed in pair/project groups"),
                }
                ensure!(h.author_member_id == signer && h.item_id == *share_id, Code::OpRejected, "share header must carry author = signer, itemId = shareId");
                ensure!(self.share(share_id)?.is_none(), Code::OpRejected, "duplicate shareId");
                self.put_share(share_id, &ShareRec {
                    signer_member: signer, signer_device: h.signer_device_id, target_member: *target_member, target_device: *target_device,
                    scopes: scopes.clone(), count: *count, sha256: *sha256, group_kind: kind, consumed: false,
                })?;
                Ok(Outcome::Applied)
            }
            Payload::TransferKey { transfer_id, target_device, size, sha256, key: tkey } => {
                let d = cx.desc.ok_or_else(|| e(Code::OpRejected, "transfer.key outside a group"))?;
                ensure!(h.author_member_id == signer && h.item_id == *transfer_id, Code::OpRejected, "transfer.key header must carry author = signer, itemId = transferId");
                let target_member = match d.kind {
                    GroupType::Pair => {
                        ensure!(cx.at.role(&signer).is_some_and(Role::is_admin), Code::OpRejected, "transfer.key in a pair only from an admin");
                        d.peer_of(&signer).ok_or_else(|| e(Code::OpRejected, "signer is not in this pair"))?
                    }
                    GroupType::Personal => {
                        ensure!(d.owner == Some(signer), Code::OpRejected, "transfer.key in a personal group only by its owner");
                        signer
                    }
                    _ => return err(Code::OpRejected, "transfer.key only in a pair (admin → member) or the own personal group"),
                };
                ensure!(h.recipient_member_id == target_member, Code::OpRejected, "transfer.key recipient must be the target member");
                ensure!(cx.head.active_device(target_device).is_some_and(|(m, _)| m.member_id == target_member), Code::OpRejected,
                    "transfer target is not an active device of the target member");
                ensure!(self.transfer(transfer_id)?.is_none(), Code::OpRejected, "duplicate transferId");
                self.db.trow_put("xfer", "transfer_id", &self.team, transfer_id, &enc(&XferRec {
                    transfer_id: *transfer_id, signer_member: signer, signer_device: h.signer_device_id, target_member, target_device: *target_device,
                    size: *size, sha256: *sha256, key: *tkey })?)?;
                Ok(Outcome::Applied)
            }
            Payload::SeqHead { .. } => {
                ensure!(h.author_member_id == signer, Code::OpRejected, "seq.head only about the signer itself");
                Ok(Outcome::Applied)
            }
            Payload::SeqSkip { device, .. } => {
                ensure!(h.author_member_id == signer, Code::OpRejected, "seq.skip only about the signer itself");
                ensure!(cx.chain.cert(device).is_some_and(|c| c.member_id == signer), Code::OpRejected, "seq.skip for a device of another member");
                Ok(Outcome::Applied)
            }
        }
    }

    fn drain_pending(&self, it: &mut ItemRec) -> Res<()> {
        loop {
            let next = it.rev + 1;
            // Among buffered ops for the next rev, the smallest (seq, device) wins; same-rev losers are dropped.
            let mut cands: Vec<(u64, Id16, usize)> = it.pending.iter().enumerate()
                .filter_map(|(i, r)| SignedOp::parse(r).ok().filter(|o| o.header.rev == next).map(|o| (o.header.seq, o.header.signer_device_id, i)))
                .collect();
            if cands.is_empty() { return Ok(()); }
            cands.sort();
            let raw = it.pending[cands[0].2].clone();
            let drop: Vec<usize> = cands.iter().map(|c| c.2).collect();
            let mut i = 0;
            it.pending.retain(|_| { let keep = !drop.contains(&i); i += 1; keep });
            let op = SignedOp::parse(&raw)?;
            let payload = Payload::parse(op.op_type()?, &op.payload)?;
            apply_rev_op(it, &payload, &raw, op.header.seq, op.header.signer_device_id)?;
        }
    }

    /// After an outbox op is resealed under a newer epoch, keep the bytes that actually travel.
    pub fn replace_op(&self, k: &ItemKey, old: &[u8], new: &[u8]) -> Res<()> {
        let Some(mut it) = self.item(k)? else { return Ok(()) };
        let mut hit = false;
        for o in it.ops.iter_mut().chain(it.tombstone.iter_mut()) { if o.as_slice() == old { *o = new.to_vec(); hit = true; } }
        if hit { self.put_item(&it)?; }
        Ok(())
    }

    pub fn item_json(&self, k: &ItemKey) -> Res<Option<J>> {
        let Some(it) = self.item(k)? else { return Ok(None) };
        let statuses = self.statuses(k)?;
        Ok(Some(item_view(&it, &statuses)?))
    }
}

/// Status transition rules (§3.0 ред. 10), independent of delivery order.
fn transition(rec: &mut StatusRec, status: StatusKind, note: Option<String>, me: Stamp, resource: bool) -> Res<Outcome> {
    if status == StatusKind::Seen {
        ensure!(note.is_none(), Code::OpRejected, "seen carries no note");
        if let Some(s) = &rec.seen {
            if me.k() >= s.k() { return err(Code::OpRejected, "seen is set only once"); }
        }
        if let Some(f) = &rec.first_final {
            ensure!(me.k() < f.k(), Code::OpRejected, "seen after a final status");
        }
        rec.seen = Some(me);
        return Ok(Outcome::Applied);
    }
    match &rec.fin {
        Some(f) if resource && me.k() > f.at.k() => return err(Code::OpRejected, format!("{} is final (accepted/declined never change)", f.status)),
        Some(f) if !resource && me.k() < f.at.k() => return Ok(Outcome::Ignored("older mark status (the last by seq wins)".into())),
        Some(f) if me.k() == f.at.k() => return Ok(Outcome::Ignored("same status already applied".into())),
        _ => {}
    }
    rec.fin = Some(FinalRec { status: status.as_str().into(), note, at: me.clone() });
    if rec.first_final.as_ref().is_none_or(|f| me.k() < f.k()) { rec.first_final = Some(me); }
    if let (Some(s), Some(f)) = (&rec.seen, &rec.first_final) { if s.k() > f.k() { rec.seen = None; } }
    Ok(Outcome::Applied)
}

fn apply_rev_op(it: &mut ItemRec, payload: &Payload, raw: &[u8], seq: u64, device: Id16) -> Res<()> {
    let prev = it.content.clone();
    let mut c = content_from(&it.content)?;
    match payload {
        Payload::Edit { delta, .. } => {
            if delta.anchor.is_some() { c.anchor = delta.anchor.clone(); }
            if delta.context.is_some() { c.context = delta.context.clone(); }
            if let Some(bd) = &delta.body {
                let k = Kind::parse(&it.kind)?;
                c.body = itemop::parse_body(k, Some(bd.clone()))?;
            }
            if delta.attachments.is_some() { c.attachments = delta.attachments.clone(); }
        }
        Payload::CollectionAdd(k) | Payload::CollectionRemove(k) => {
            let (mut keys, title): (Vec<ItemKey>, Option<Value>) = match &c.body {
                Some(bd) => {
                    let mut f = Fields::new(bd.clone(), "collection")?;
                    let keys = f.req("itemKeys")?.array()?.into_iter().map(itemop::item_key_from).collect::<Res<_>>()?;
                    (keys, itemop::collection_title(&mut f)?)
                }
                None => (vec![], None),
            };
            if matches!(payload, Payload::CollectionAdd(_)) { if !keys.contains(k) { keys.push(*k); } } else { keys.retain(|x| x != k); }
            c.body = Some(MapB::new().put("itemKeys", Value::A(keys.iter().map(itemop::item_key_value).collect())).put_opt("title", title).build());
        }
        _ => unreachable!(),
    }
    it.content = content_map(&c);
    it.rev += 1;
    it.ops.push(raw.to_vec());
    it.head = Some(RevHead { seq, device, prev_content: prev });
    Ok(())
}

pub fn status_json(recipient: &Id16, s: &StatusRec) -> J {
    json!({"recipient": hex(recipient),
        "seen": s.seen.as_ref().map(|x| json!({"seq": x.seq, "device": hex(&x.device)})),
        "status": s.fin.as_ref().map(|f| f.status.clone()), "note": s.fin.as_ref().and_then(|f| f.note.clone()),
        "seq": s.fin.as_ref().map(|f| f.at.seq), "device": s.fin.as_ref().map(|f| hex(&f.at.device))})
}

pub fn item_view(it: &ItemRec, statuses: &[(Id16, StatusRec)]) -> Res<J> {
    let content = content_from(&it.content)?;
    let cj = |v: &Option<Value>| v.as_ref().map(cbor_json).unwrap_or(J::Null);
    let replaced = match &it.replaced {
        Some(r) => { let c = content_from(&r.content)?; json!({"rev": r.rev, "byDevice": hex(&r.by_device), "anchor": cj(&c.anchor), "context": cj(&c.context), "body": cj(&c.body)}) }
        None => J::Null,
    };
    Ok(json!({
        "author": hex(&it.author), "itemId": hex(&it.item_id), "kind": it.kind, "rev": it.rev, "createdAt": it.created_at,
        "space": it.space.as_ref().map(|s| hex(s)), "recipient": it.recipient.map(|r| hex(&r)), "home": it.home,
        "deleted": it.tombstone.is_some(), "anchor": cj(&content.anchor), "context": cj(&content.context),
        "body": cj(&content.body), "attachments": cj(&content.attachments),
        "storedOps": it.ops.len(), "bufferedOps": it.pending.len(), "replacedByOtherDevice": replaced,
        "statuses": statuses.iter().map(|(r, s)| status_json(r, s)).collect::<Vec<_>>(),
    }))
}

trait OptRow<T> { fn optional_row(self) -> Res<Option<T>>; }
impl<T> OptRow<T> for Result<T, rusqlite::Error> {
    fn optional_row(self) -> Res<Option<T>> {
        match self { Ok(v) => Ok(Some(v)), Err(rusqlite::Error::QueryReturnedNoRows) => Ok(None), Err(x) => Err(sq(x)) }
    }
}

/// Debug/JSON view of a CBOR value (bytes as hex).
pub fn cbor_json(v: &Value) -> J {
    match v {
        Value::U(n) => json!(n),
        Value::N(n) => json!(-1 - (*n as i128) as i64),
        Value::B(b) => json!(hex(b)),
        Value::T(s) => json!(s),
        Value::A(a) => J::Array(a.iter().map(cbor_json).collect()),
        Value::M(m) => J::Object(m.iter().map(|(k, v)| (match k { Value::T(s) => s.clone(), other => format!("{other:?}") }, cbor_json(v))).collect()),
        Value::Bool(b) => json!(b),
        Value::Null => J::Null,
    }
}

/// JSON → CBOR for payload construction: strings prefixed `hex:` become bytes, other strings
/// text, unsigned integers ints, objects maps, arrays arrays. Floats/negatives are rejected.
pub fn json_cbor(j: &J) -> Res<Value> {
    Ok(match j {
        J::Null => Value::Null,
        J::Bool(b) => Value::Bool(*b),
        J::Number(n) => Value::U(n.as_u64().ok_or_else(|| e(Code::InvalidInput, "only unsigned integers in payloads"))?),
        J::String(s) => match s.strip_prefix("hex:") { Some(h) => Value::B(util::unhex(h)?), None => Value::T(s.clone()) },
        J::Array(a) => Value::A(a.iter().map(json_cbor).collect::<Res<_>>()?),
        J::Object(o) => Value::M(o.iter().map(|(k, v)| Ok((Value::T(k.clone()), json_cbor(v)?))).collect::<Res<_>>()?),
    })
}
