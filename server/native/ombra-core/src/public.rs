//! Server "public verification" of MLS handshake (§1, §3.1 N2, D-10): the server tracks each group as
//! an OpenMLS `PublicGroup` (tree + GroupContext, no secrets) and runs the *same* commit policy as
//! clients (`mls::validate_commit`) against its verified roster chain. This is a barrier against
//! garbage and against a third party "occupying" a pair groupId — clients still verify everything.
//!
//! Flow: the creator uploads the epoch-0 GroupInfo (with ratchet tree) once — `init` checks the
//! GroupInfo signature, `groupId = SHA-256(descriptor)`, the extension set, epoch 0 with exactly one
//! leaf (the creator, hence the GroupInfo signer), that the leaf is admissible for the descriptor in
//! the named roster version (so for pair/personal groups only a descriptor member's device can create
//! the group) and — the same rule as client-side `create_group` — that a team/project group's creator
//! is owner/admin in that version and in the head (N2: nobody else can squat the deterministic
//! team/project groupId). Each later commit goes through `commit`.
//! The GroupInfo is kept server-side only and never served (external commits are rejected anyway).

use std::collections::{BTreeSet, HashMap};

use openmls::prelude::{tls_codec::Deserialize as _, *};
use openmls_memory_storage::MemoryStorage;
use openmls_rust_crypto::RustCrypto;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value as J};

use crate::descriptor::Descriptor;
use crate::ensure;
use crate::mls::{self, GroupView};
use crate::roster::Chain;
use crate::util::{self, e, hex, hexser, Code, Res, H32};

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PgState {
    #[serde(with = "hexser")] pub group_id: H32,
    #[serde(with = "hexser")] pub descriptor: Vec<u8>,
    pub roster_version: u64,
    storage: Vec<(String, String)>,
}

fn rej(m: impl Into<String>) -> util::Error { e(Code::CommitRejected, m) }

fn dump(s: &MemoryStorage) -> Vec<(String, String)> {
    let mut v: Vec<_> = s.values.read().expect("lock").iter().map(|(k, v)| (hex(k), hex(v))).collect();
    v.sort();
    v
}

fn load(st: &PgState) -> Res<MemoryStorage> {
    let mut m = HashMap::new();
    for (k, v) in &st.storage { m.insert(util::unhex(k)?, util::unhex(v)?); }
    Ok(MemoryStorage { values: std::sync::RwLock::new(m) })
}

fn leaves(pg: &PublicGroup) -> Vec<String> { pg.members().map(|m| hex(m.credential.serialized_content())).collect() }

pub fn init(chain: &Chain, group_info: &[u8]) -> Res<(PgState, J)> {
    let msg = MlsMessageIn::tls_deserialize_exact(group_info).map_err(|x| rej(format!("malformed GroupInfo: {x:?}")))?;
    let MlsMessageBodyIn::GroupInfo(vgi) = msg.extract() else { return Err(rej("not a GroupInfo")) };
    let tree = vgi.extensions().ratchet_tree().ok_or_else(|| rej("GroupInfo without ratchet tree"))?.ratchet_tree().clone();
    let storage = MemoryStorage::default();
    let (pg, _) = PublicGroup::from_external(&RustCrypto::default(), &storage, tree, vgi, ProposalStore::new())
        .map_err(|x| rej(format!("GroupInfo/tree invalid: {x:?}")))?;
    let gc = pg.group_context();
    let dbytes = gc.extensions().unknown(mls::DESC_EXT).ok_or_else(|| rej("descriptor extension missing"))?.0.clone();
    let desc = Descriptor::parse(&dbytes)?;
    ensure!(gc.group_id().as_slice() == desc.group_id().as_slice(), Code::CommitRejected, "groupId != SHA-256(descriptor)");
    ensure!(desc.team_id == chain.team_id, Code::CommitRejected, "group of another team");
    let v = mls::check_gc(gc, &dbytes, chain, 1, Code::CommitRejected)?;
    ensure!(gc.epoch().as_u64() == 0, Code::CommitRejected, "only the creator's epoch-0 GroupInfo registers a group");
    let members: Vec<_> = pg.members().collect();
    ensure!(members.len() == 1, Code::CommitRejected, "an epoch-0 group has exactly the creator's leaf");
    let adm = mls::admitted(&desc, chain.version(v).unwrap());
    if let Some(m) = members.iter().find(|m| mls::leaf_adm(&adm, &m.credential, &m.signature_key).is_none()) {
        return Err(rej(format!("leaf {} is not admissible for this {} group (N2)", hex(m.credential.serialized_content()), desc.kind.as_str())));
    }
    let creator = util::id16(members[0].credential.serialized_content())?;
    if desc.kind.admin_scope() {
        let admin_in = |p: &crate::roster::Projection| p.active_device(&creator).is_some_and(|(m, _)| m.role.is_admin());
        ensure!(admin_in(chain.version(v).unwrap()) && admin_in(chain.head()), Code::CommitRejected,
            "team/project groups are created by an admin: creator {} is not owner/admin in v{v} and the roster head", hex(&creator));
    }
    let gid: H32 = gc.group_id().as_slice().try_into().map_err(|_| rej("groupId length"))?;
    let out = json!({"groupId": hex(&gid), "epoch": gc.epoch().as_u64(), "rosterVersion": v, "leaves": leaves(&pg),
        "committer": hex(&creator), "descriptor": serde_json::to_value(&desc).unwrap()});
    Ok((PgState { group_id: gid, descriptor: dbytes, roster_version: v, storage: dump(&storage) }, out))
}

pub fn commit(chain: &Chain, mut st: PgState, msg: &[u8]) -> Res<(PgState, J)> {
    let storage = load(&st)?;
    let mut pg = PublicGroup::load(&storage, &GroupId::from_slice(&st.group_id)).map_err(|x| e(Code::Storage, format!("{x:?}")))?
        .ok_or_else(|| e(Code::NotFound, "public group state missing"))?;
    let mi = MlsMessageIn::tls_deserialize_exact(msg).map_err(|x| rej(format!("malformed: {x:?}")))?;
    let pm = mi.try_into_protocol_message().map_err(|x| rej(format!("{x:?}")))?;
    ensure!(matches!(pm, ProtocolMessage::PublicMessage(_)), Code::CommitRejected, "handshake must be a PublicMessage (D-10)");
    let processed = pg.process_message(&RustCrypto::default(), pm).map_err(|x| rej(format!("{x:?}")))?;
    let sender = processed.sender().clone();
    let ProcessedMessageContent::StagedCommitMessage(sc) = processed.into_content() else { return Err(rej("only commits are accepted")) };
    let none = BTreeSet::new();
    let gv = GroupView { descriptor: &st.descriptor, roster_version: st.roster_version, chain, frozen: &none, members: pg.members().collect() };
    let verdict = mls::validate_commit(&gv, &sender, &sc)?;
    pg.merge_commit(&storage, *sc).map_err(|x| e(Code::Storage, format!("{x:?}")))?;
    st.roster_version = verdict.version;
    st.storage = dump(&storage);
    let out = json!({"groupId": hex(&st.group_id), "epoch": pg.group_context().epoch().as_u64(), "rosterVersion": verdict.version,
        "committer": hex(&verdict.committer), "added": verdict.added_json(), "leaves": leaves(&pg)});
    Ok((st, out))
}
