//! Durable outbox (design-sync-decision §3). An own op is signed and applied to the replica when it is queued
//! (`enqueue`, works offline) and kept as an outbox row. `seal` MLS-encrypts it once (the ratchet step and the
//! ciphertext commit in the same transaction); every retry sends the very same bytes. After an unambiguous 409
//! epoch-conflict (`conflict`) the ciphertext is dropped and the next `seal` re-signs the op under the new epoch with
//! the same opId/seq/rev (the server rolled back its dedup row). `sent` deletes the row; `drop` deletes it and writes a
//! security-log entry (final server rejection).
//!
//! Row (team key, pk = opId): JSON `Row`; plaintext columns: `ord` (queue order), `state` (0 queued, 1 sealed).

use rusqlite::{params, OptionalExtension};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value as J};

use crate::cbor;
use crate::itemop::{OpType, SignedOp};
use crate::mls::{self, OpSpec};
use crate::replica::Replica;
use crate::state::{Provider, State};
use crate::store::{sq, Db};
use crate::util::{self, e, hex, hexopt, hexser, Code, Id16, Res, H32};

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Row {
    #[serde(with = "hexser")] group_id: H32,
    #[serde(with = "hexser")] team_id: H32,
    op_type: String,
    #[serde(default, with = "hexopt")] author: Option<Id16>,
    #[serde(with = "hexser")] item_id: Id16,
    rev: u64,
    #[serde(default, with = "hexopt")] recipient: Option<Id16>,
    #[serde(with = "hexser")] payload: Vec<u8>,
    #[serde(with = "hexser")] op_id: Id16,
    seq: u64,
    /// Signed op as applied locally / last sealed.
    #[serde(with = "hexser")] raw: Vec<u8>,
    #[serde(default, with = "hexopt")] message: Option<Vec<u8>>,
    /// Signed server envelope carrying `message` (its opId = this opId): kept so a retry is byte-identical
    /// (the server's opId dedup compares request hashes).
    #[serde(default, with = "hexopt")] envelope: Option<Vec<u8>>,
    #[serde(default, with = "hexopt")] envelope_sig: Option<Vec<u8>>,
}

fn load(db: &Db, op_id: &Id16) -> Res<(Row, i64)> {
    let r: Option<(Vec<u8>, Vec<u8>, i64)> = db.conn.query_row("SELECT team_id, v, ord FROM outbox WHERE op_id = ?1", [&op_id[..]],
        |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?))).optional().map_err(sq)?;
    let (t, v, ord) = r.ok_or_else(|| e(Code::NotFound, format!("opId {} not in outbox", hex(op_id))))?;
    let team = util::h32(&t)?;
    let pt = db.team_open(&team, "outbox", op_id, &v)?;
    Ok((serde_json::from_slice(&pt).map_err(|x| e(Code::Storage, format!("outbox: {x}")))?, ord))
}

fn store(db: &Db, row: &Row, ord: i64) -> Res<()> {
    let pt = serde_json::to_vec(row).map_err(|x| e(Code::Internal, x.to_string()))?;
    let v = db.team_seal(&row.team_id, "outbox", &row.op_id, &pt)?;
    db.conn.execute("INSERT OR REPLACE INTO outbox (op_id, team_id, ord, state, v) VALUES (?1, ?2, ?3, ?4, ?5)",
        params![&row.op_id[..], &row.team_id[..], ord, row.message.is_some() as i64, v]).map_err(sq)?;
    Ok(())
}

fn spec(row: &Row) -> Res<OpSpec> {
    Ok(OpSpec { op: OpType::parse(&row.op_type)?, author: row.author, item_id: row.item_id, rev: row.rev, recipient: row.recipient,
        payload: cbor::decode(&row.payload)?, op_id: row.op_id, seq: Some(row.seq) })
}

/// Sign + apply locally + queue. Control ops (seq.head/seq.skip) are queued too.
pub fn enqueue(st: &mut State, p: &Provider, db: &Db, gid: &H32, spec: OpSpec, now: u64) -> Res<J> {
    let (op, raw, outcome) = mls::apply_own(st, p, db, gid, &spec, now)?;
    let team_id = st.group(gid)?.team_id;
    let ord: i64 = db.conn.query_row("SELECT COALESCE(MAX(ord), 0) + 1 FROM outbox", [], |r| r.get(0)).map_err(sq)?;
    let row = Row { group_id: *gid, team_id, op_type: spec.op.name().into(), author: spec.author, item_id: spec.item_id, rev: spec.rev,
        recipient: spec.recipient, payload: cbor::encode(&op.payload), op_id: op.op_id, seq: op.header.seq, raw, message: None,
        envelope: None, envelope_sig: None };
    store(db, &row, ord)?;
    Ok(json!({"opId": hex(&op.op_id), "groupId": hex(gid), "itemId": hex(&spec.item_id), "rev": spec.rev, "seq": op.header.seq, "outcome": outcome}))
}

/// The ciphertext to (re)send. Same bytes on every call until `conflict`.
pub fn seal(st: &mut State, p: &Provider, db: &Db, op_id: &Id16) -> Res<J> {
    let (mut row, ord) = load(db, op_id)?;
    if row.message.is_none() {
        let old = SignedOp::parse(&row.raw)?;
        let epoch = mls::load_group(p, &row.group_id)?.epoch().as_u64();
        let version = st.group(&row.group_id)?.roster_version;
        let (op, raw) = if old.header.epoch == epoch && old.header.roster_version == version { (old, row.raw.clone()) } else {
            let (op, raw) = mls::build_op(st, p, &row.group_id, &spec(&row)?)?;
            let chain = &st.team(&row.team_id)?.chain;
            let at = crate::replica::verify_at_version(chain, &op)?;
            crate::replica::check_signed_rights(chain, at, &op)?;
            Replica::new(db, row.team_id).replace_op(&op.item_key(), &row.raw, &raw)?;
            (op, raw)
        };
        row.message = Some(mls::encrypt(st, p, &row.group_id, &op, &raw)?);
        row.raw = raw;
        store(db, &row, ord)?;
    }
    let epoch = SignedOp::parse(&row.raw)?.header.epoch;
    Ok(json!({"opId": hex(op_id), "groupId": hex(&row.group_id), "teamId": hex(&row.team_id), "seq": row.seq, "epoch": epoch,
        "message": hex(row.message.as_ref().unwrap()), "envelope": row.envelope.as_ref().map(|x| hex(x)), "signature": row.envelope_sig.as_ref().map(|x| hex(x))}))
}

/// Remember the signed envelope of the sealed message (`None` clears it: the envelope went stale, sign a fresh one).
pub fn set_envelope(db: &Db, op_id: &Id16, env: Option<(Vec<u8>, Vec<u8>)>) -> Res<J> {
    let (mut row, ord) = load(db, op_id)?;
    crate::ensure!(row.message.is_some(), Code::InvalidInput, "op not sealed");
    (row.envelope, row.envelope_sig) = match env { Some((e, s)) => (Some(e), Some(s)), None => (None, None) };
    store(db, &row, ord)?;
    Ok(json!({"opId": hex(op_id)}))
}

/// 409 epoch-conflict: forget the ciphertext; the next `seal` re-signs under the current epoch (same opId/seq/rev).
pub fn conflict(db: &Db, op_id: &Id16) -> Res<J> {
    let (mut row, ord) = load(db, op_id)?;
    row.message = None;
    row.envelope = None;
    row.envelope_sig = None;
    store(db, &row, ord)?;
    Ok(json!({"opId": hex(op_id)}))
}

pub fn sent(db: &Db, op_id: &Id16) -> Res<J> {
    let n = db.conn.execute("DELETE FROM outbox WHERE op_id = ?1", [&op_id[..]]).map_err(sq)?;
    Ok(json!({"removed": n == 1}))
}

pub fn drop_op(db: &Db, op_id: &Id16, reason: &str, now: u64) -> Res<J> {
    let (row, _) = load(db, op_id)?;
    db.conn.execute("DELETE FROM outbox WHERE op_id = ?1", [&op_id[..]]).map_err(sq)?;
    db.seclog_add(Some(&row.team_id), now, &json!({"event": "own-op-rejected", "opId": hex(op_id), "opType": row.op_type,
        "itemId": hex(&row.item_id), "reason": reason}))?;
    Ok(json!({"dropped": true}))
}

/// Queued ops in send order (only of unlocked teams).
pub fn list(db: &Db, team: Option<&H32>) -> Res<J> {
    let mut q = db.conn.prepare("SELECT op_id, team_id, state FROM outbox WHERE (?1 IS NULL OR team_id = ?1) ORDER BY ord").map_err(sq)?;
    let rows = q.query_map(params![team.map(|t| t.to_vec())], |r| Ok((r.get::<_, Vec<u8>>(0)?, r.get::<_, Vec<u8>>(1)?, r.get::<_, i64>(2)?))).map_err(sq)?;
    let mut out = vec![];
    for r in rows {
        let (op, t, s) = r.map_err(sq)?;
        let t = util::h32(&t)?;
        if !db.is_unlocked(&t) { continue; }
        let (row, _) = load(db, &util::id16(&op)?)?;
        out.push(json!({"opId": hex(&op), "teamId": hex(&t), "groupId": hex(&row.group_id), "opType": row.op_type, "itemId": hex(&row.item_id), "sealed": s == 1}));
    }
    Ok(json!({"ops": out}))
}
