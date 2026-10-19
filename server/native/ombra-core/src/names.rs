//! Member and device names (SD-16, R2-07): `privatePart {salt, names, devices}` travels only inside the
//! team MLS group; the roster projection carries `privateCommit = SHA-256(salt ‖ canonical(privatePart))`.
//! A received part is kept only when it matches the commitment of a verified roster version, so the
//! server can neither read nor substitute a name; until then the client shows "names not confirmed".
//!
//! Wire form of the MLS application message: `MAGIC ‖ canonical CBOR {salt: bstr32, names: {hex(memberId): tstr},
//! devices: {hex(deviceId): tstr}}`. The prefix cannot start a SignedOp (canonical CBOR map), so the
//! incoming application path tries [`try_open`] first.

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};
use serde_json::{json, Value as J};

use crate::cbor::{self, MapB, Value};
use crate::ensure;
use crate::state::{Provider, State};
use crate::util::{self, e, err, hex, Code, Res, H32};

pub const MAGIC: &[u8] = b"ombra/v1/names\0";
/// Longest name, in characters.
pub const MAX_NAME: usize = 64;

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrivatePart {
    #[serde(with = "crate::util::hexser")] pub salt: H32,
    /// hex(memberId) → display name.
    pub names: BTreeMap<String, String>,
    /// hex(deviceId) → device name.
    pub devices: BTreeMap<String, String>,
}

impl PrivatePart {
    fn cbor(&self) -> Value {
        let map = |m: &BTreeMap<String, String>| Value::M(m.iter().map(|(k, v)| (cbor::t(k), cbor::t(v))).collect());
        MapB::new().put("salt", cbor::b(&self.salt)).put("names", map(&self.names)).put("devices", map(&self.devices)).build()
    }

    /// The canonical `privatePart` without the salt (what `roster.private_commit` hashes).
    fn part(&self) -> Value {
        let map = |m: &BTreeMap<String, String>| Value::M(m.iter().map(|(k, v)| (cbor::t(k), cbor::t(v))).collect());
        MapB::new().put("names", map(&self.names)).put("devices", map(&self.devices)).build()
    }

    pub fn commit(&self) -> H32 { util::sha256(&[&self.salt, &cbor::encode(&self.part())]) }

    pub fn wire(&self) -> Vec<u8> { [MAGIC, &cbor::encode(&self.cbor())].concat() }

    fn parse(bytes: &[u8]) -> Res<PrivatePart> {
        let bad = |x: cbor::CborError| e(Code::InvalidInput, format!("names: {x}"));
        let mut m = cbor::Fields::new(cbor::decode(bytes).map_err(bad)?, "names").map_err(bad)?;
        let salt = match m.req("salt").map_err(bad)? { Value::B(b) => util::h32(&b)?, _ => return err(Code::InvalidInput, "names: salt") };
        let strs = |v: Value| -> Res<BTreeMap<String, String>> {
            let Value::M(m) = v else { return err(Code::InvalidInput, "names: map expected") };
            m.into_iter().map(|(k, v)| match (k, v) {
                (Value::T(k), Value::T(v)) => { check_name(&v)?; Ok((k, v)) }
                _ => err(Code::InvalidInput, "names: text keys and values"),
            }).collect()
        };
        let names = strs(m.req("names").map_err(bad)?)?;
        let devices = strs(m.req("devices").map_err(bad)?)?;
        m.finish().map_err(bad)?;
        Ok(PrivatePart { salt, names, devices })
    }
}

fn check_name(n: &str) -> Res<()> {
    ensure!(!n.trim().is_empty() && n.trim() == n, Code::InvalidInput, "name must be non-empty without surrounding spaces");
    ensure!(n.chars().count() <= MAX_NAME, Code::InvalidInput, "name longer than {MAX_NAME} characters");
    ensure!(!n.chars().any(char::is_control), Code::InvalidInput, "name must not contain control characters");
    Ok(())
}

/// Newest verified roster version whose `privateCommit` is this part's (never the all-zero "no names" commit).
fn matched_version(state: &State, team: &H32, part: &PrivatePart) -> Res<Option<u64>> {
    let chain = &state.team(team)?.chain;
    let c = part.commit();
    Ok((1..=chain.head_version()).rev().find(|v| chain.version(*v).is_some_and(|p| p.private_commit != [0; 32] && util::ct_eq(&p.private_commit, &c))))
}

/// `names.get {teamId}` → `{confirmed, version, names, devices}`: confirmed when the kept part is the head's
/// commitment (or the head commits to no names at all); otherwise the names shown are those of `version`.
pub fn get(state: &State, team: &H32) -> Res<J> {
    let t = state.team(team)?;
    let head = t.chain.head();
    let kept = t.names.clone();
    let version = match &kept { Some(p) => matched_version(state, team, p)?, None => None };
    let confirmed = match &kept {
        Some(p) => util::ct_eq(&p.commit(), &head.private_commit),
        None => head.private_commit == [0; 32],
    };
    let p = kept.unwrap_or_default();
    Ok(json!({"confirmed": confirmed, "version": version, "names": p.names, "devices": p.devices}))
}

/// `names.set {teamId, name?, devices?: {hex(deviceId): name|null}}`: this member's own name and its own
/// devices' names (nobody renames others). Starts from the confirmed part (refused while names are
/// unconfirmed, which would drop the others'), drops members no longer in the head, fresh salt; kept
/// locally at once. Returns `{privateCommit}` for `roster.update`, then `names.seal` sends it.
pub fn set(state: &mut State, team: &H32, name: Option<&str>, devices: &BTreeMap<String, Option<String>>) -> Res<J> {
    let me = state.device()?.member_id;
    let current = get(state, team)?;
    ensure!(current["confirmed"].as_bool() == Some(true), Code::Frozen, "names are not confirmed yet: wait for the team's names before changing yours");
    let t = state.team(team)?;
    let head = t.chain.head();
    let mine = head.members.iter().find(|m| m.member_id == me).ok_or_else(|| e(Code::NotFound, "not a member of this team"))?;
    let mut p = t.names.clone().unwrap_or_default();
    p.names.retain(|k, _| head.members.iter().any(|m| hex(&m.member_id) == *k));
    p.devices.retain(|k, _| head.members.iter().flat_map(|m| m.devices.iter()).any(|d| d.revoked.is_none() && hex(&d.device_id) == *k));
    if let Some(n) = name { check_name(n)?; p.names.insert(hex(&me), n.to_string()); }
    for (id, n) in devices {
        ensure!(mine.devices.iter().any(|d| hex(&d.device_id) == *id), Code::InvalidInput, "device {id} is not one of this member's");
        match n { Some(n) => { check_name(n)?; p.devices.insert(id.clone(), n.clone()); } None => { p.devices.remove(id); } }
    }
    p.salt = util::random::<32>();
    let commit = p.commit();
    state.team_mut(team)?.names = Some(p);
    Ok(json!({"privateCommit": hex(&commit)}))
}

/// `names.seal {groupId}`: the kept part of the group's team as an MLS application message of that team
/// group (after the roster carrying its commitment was published, and to a newcomer after its Welcome).
pub fn seal(state: &State, p: &Provider, gid: &H32) -> Res<J> {
    let meta = state.group(gid)?;
    ensure!(!meta.closed && !meta.evicted, Code::Frozen, "group closed");
    let team = state.team(&meta.team_id)?;
    ensure!(!team.removed, Code::Frozen, "removed from team");
    let part = team.names.as_ref().ok_or_else(|| e(Code::NotFound, "no names to send"))?;
    let mut group = crate::mls::load_group(p, gid)?;
    group.set_aad(meta.roster_version.to_be_bytes().to_vec());
    let msg = group.create_message(p, &crate::mls::signer(state, p)?, &part.wire()).map_err(|x| e(Code::Mls, format!("{x:?}")))?;
    Ok(json!({"message": hex(&msg.to_bytes().map_err(|x| e(Code::Mls, format!("{x:?}")))?)}))
}

/// Incoming application message of a team group: `None` when it is not a names message (an item op).
/// Kept when it matches a verified version not older than the kept part's; a part matching no version yet
/// is `need-roster` (retried after the roster catches up, not acked).
pub fn try_open(state: &mut State, gid: &H32, plaintext: &[u8]) -> Option<Res<J>> {
    let body = plaintext.strip_prefix(MAGIC)?;
    Some((|| {
        let team = state.group(gid)?.team_id;
        let part = PrivatePart::parse(body)?;
        let Some(v) = matched_version(state, &team, &part)? else { return err(Code::NeedRoster, "names match no verified roster version yet") };
        let kept = match state.team(&team)?.names.clone() { Some(k) => matched_version(state, &team, &k)?, None => None };
        let newer = kept.is_none_or(|k| v >= k);
        if newer { state.team_mut(&team)?.names = Some(part); }
        let head = state.team(&team)?.chain.head_version();
        Ok(json!({"kind": "names", "version": v, "kept": newer, "confirmed": newer && v == head}))
    })())
}
