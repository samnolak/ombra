//! OpenMLS integration and the staged-commit / Welcome / KeyPackage policy (§3.1, SD-01/03/04/11,
//! R2-01/12, R5-01/02, N2/N9/N10). Ciphersuite MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519,
//! BasicCredential(identity = deviceId[16]); handshake as PublicMessage, application as
//! PrivateMessage.
//!
//! GroupContext carries two extensions declared in RequiredCapabilities:
//! `ombra_roster_hash` 0xF0A1 (hash of a verified roster version) and `ombra_group_descriptor`
//! 0xF0A2 (immutable canonical descriptor, `groupId = SHA-256(descriptor)`). No other GroupContext
//! extension is accepted.
//!
//! Commit policy (receiver side; the sender runs the same check on its own staged commit before
//! merging, so a local client never emits what peers would reject). With V = roster version
//! named by the resulting GroupContext (must be in the verified chain, >= the group's current
//! version) and A(V) = admissible leaves of the group type in V:
//! - sender is a Member leaf (no external commit/NewMemberCommit), whose deviceId + signature key
//!   match a device in A(V);
//! - proposals ⊆ {Add, Remove, Update, GroupContextExtensions};
//! - Add: team/project only by owner/admin; pair/personal by a participant (implied by A(V));
//!   added KeyPackage ∈ A(V) (deviceId + key), not a frozen pair peer (N9);
//! - Remove: only leaves NOT in A(V) (R2-12, R5-02) — any leaf may remove them;
//! - Update / UpdatePath: credential unchanged, key = the roster key of that device;
//! - GCE: team/project only by owner/admin; descriptor bytes unchanged; roster hash only to a
//!   verified version;
//! - after the commit every leaf ∈ A(V).

use std::collections::{BTreeMap, BTreeSet};

use openmls::prelude::{tls_codec::*, *};
use openmls_basic_credential::SignatureKeyPair;
use openmls_traits::OpenMlsProvider;
use serde_json::{json, Value as J};

use crate::descriptor::{Descriptor, GroupType};
use crate::ensure;
use crate::heartbeat::{self, HbGroup, Heartbeat, HB_EXPORTER_LABEL};
use crate::itemop::{OpType, Payload, SignedOp, Header, ZERO16};
use crate::replica::{self, ApplyCtx, LiveCtx};
use crate::roster::{Chain, Projection, Role};
use crate::store::Db;
use crate::state::{CountedHb, GroupMeta, Provider, State, TeamState};
use crate::util::{self, e, err, hex, Code, Id16, Res, H32};

pub const CS: Ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;
pub const ROSTER_EXT: u16 = 0xF0A1;
pub const DESC_EXT: u16 = 0xF0A2;
/// SD-11: KeyPackage lifetime at most 30 days (+ OpenMLS' default 1 h back-dating margin).
pub const KP_MAX_LIFETIME: u64 = 30 * 24 * 3600 + 3600;
/// Handshake (commits, proposals) outgoing as PublicMessage (D-10); application messages are always
/// PrivateMessage in OpenMLS.
pub const WIRE_POLICY: WireFormatPolicy = MIXED_PLAINTEXT_WIRE_FORMAT_POLICY;

pub fn caps() -> Capabilities {
    Capabilities::builder().extensions(vec![ExtensionType::Unknown(ROSTER_EXT), ExtensionType::Unknown(DESC_EXT), ExtensionType::LastResort]).build()
}

pub fn gc_extensions(roster_hash: &[u8], descriptor: &[u8]) -> Res<Extensions<GroupContext>> {
    Extensions::from_vec(vec![
        Extension::RequiredCapabilities(RequiredCapabilitiesExtension::new(
            &[ExtensionType::Unknown(ROSTER_EXT), ExtensionType::Unknown(DESC_EXT)], &[], &[])),
        Extension::Unknown(ROSTER_EXT, UnknownExtension(roster_hash.to_vec())),
        Extension::Unknown(DESC_EXT, UnknownExtension(descriptor.to_vec())),
    ]).map_err(|x| e(Code::Internal, x.to_string()))
}

pub fn join_config() -> MlsGroupJoinConfig {
    MlsGroupJoinConfig::builder()
        .use_ratchet_tree_extension(true)
        .wire_format_policy(WIRE_POLICY)
        .build()
}

fn mls<E: std::fmt::Debug>(x: E) -> util::Error { e(Code::Mls, format!("{x:?}")) }

pub fn signer(state: &State, p: &Provider) -> Res<SignatureKeyPair> {
    let d = state.device()?;
    SignatureKeyPair::read(p.storage(), &d.mls_public, CS.signature_algorithm())
        .ok_or_else(|| e(Code::Storage, "MLS signature key missing from storage"))
}

pub fn credential(state: &State) -> Res<CredentialWithKey> {
    let d = state.device()?;
    Ok(CredentialWithKey { credential: BasicCredential::new(d.device_id.to_vec()).into(), signature_key: d.mls_public.clone().into() })
}

pub fn load_group(p: &Provider, gid: &H32) -> Res<MlsGroup> {
    MlsGroup::load(p.storage(), &GroupId::from_slice(gid)).map_err(mls)?.ok_or_else(|| e(Code::NotFound, "MLS group state missing"))
}

// ---------------- admissible leaves ----------------

#[derive(Clone, Debug)]
pub struct Adm { pub member: Id16, pub role: Role, pub mls_key_hash: H32 }

/// A(V): admissible leaves of a group of this descriptor in roster version `p` (R5-01).
pub fn admitted(desc: &Descriptor, p: &Projection) -> BTreeMap<Id16, Adm> {
    let mut out = BTreeMap::new();
    for m in &p.members {
        let ok = match desc.kind {
            GroupType::Team => true,
            GroupType::Project => desc.project_id.is_some_and(|pid| p.access_level(&pid, &m.member_id).is_some()),
            GroupType::Pair => desc.members.contains(&m.member_id),
            GroupType::Personal => desc.owner == Some(m.member_id),
            GroupType::Admin => m.role.is_admin(),
        };
        if !ok { continue; }
        for d in m.devices.iter().filter(|d| d.active()) {
            out.insert(d.device_id, Adm { member: m.member_id, role: m.role, mls_key_hash: d.mls_key_hash });
        }
    }
    out
}

fn leaf_id(cred: &Credential) -> Option<Id16> { cred.serialized_content().try_into().ok() }

/// The admissible entry for a leaf: deviceId (credential) AND signature key must match.
pub fn leaf_adm<'a>(adm: &'a BTreeMap<Id16, Adm>, cred: &Credential, sig: &[u8]) -> Option<(Id16, &'a Adm)> {
    let id = leaf_id(cred)?;
    adm.get(&id).filter(|a| a.mls_key_hash == util::sha256(&[sig])).map(|a| (id, a))
}

fn name(cred: &Credential) -> String { hex(cred.serialized_content()) }

/// GroupContext extension check; returns the roster version named by the roster hash.
pub fn check_gc(gc: &GroupContext, descriptor: &[u8], chain: &Chain, min_version: u64, code: Code) -> Res<u64> {
    let exts = gc.extensions();
    for x in exts.iter() {
        match x.extension_type() {
            ExtensionType::RequiredCapabilities => {}
            ExtensionType::Unknown(t) if t == ROSTER_EXT || t == DESC_EXT => {}
            other => return err(code, format!("GroupContext extension {other:?} not allowed")),
        }
    }
    let rc = exts.required_capabilities().ok_or_else(|| e(code, "RequiredCapabilities missing"))?;
    ensure!(rc.extension_types().contains(&ExtensionType::Unknown(ROSTER_EXT)) && rc.extension_types().contains(&ExtensionType::Unknown(DESC_EXT)),
        code, "RequiredCapabilities must list ombra_roster_hash and ombra_group_descriptor");
    let d = exts.unknown(DESC_EXT).ok_or_else(|| e(code, "group descriptor extension missing"))?;
    ensure!(d.0 == descriptor, code, "GroupContext descriptor differs from the group's immutable descriptor");
    let h = exts.unknown(ROSTER_EXT).ok_or_else(|| e(code, "roster hash extension missing"))?;
    let v = chain.version_of_hash(&h.0).ok_or_else(|| e(Code::NeedRoster, format!("GroupContext names roster hash {} not in the verified chain", hex(&h.0))))?;
    ensure!(v >= min_version, code, "GroupContext roster v{v} older than v{min_version}");
    Ok(v)
}

pub struct CommitVerdict { pub version: u64, pub committer: Id16, pub has_path: bool, pub added: Vec<(Id16, Vec<u8>)> }

impl CommitVerdict {
    /// `[{deviceId, kpRef}]` of the Adds (the server counts a KeyPackage as used only here, N1).
    pub fn added_json(&self) -> J {
        J::Array(self.added.iter().map(|(d, r)| json!({"deviceId": hex(d), "kpRef": hex(r)})).collect())
    }
}

/// What the commit policy needs about a group, for both a member's `MlsGroup` and the server's
/// `PublicGroup` (public-verification mode).
pub struct GroupView<'a> {
    pub descriptor: &'a [u8],
    /// Roster version named by the current (pre-commit) GroupContext.
    pub roster_version: u64,
    pub chain: &'a Chain,
    /// N9 frozen pair peers (hex memberIds); empty on the server.
    pub frozen: &'a BTreeSet<String>,
    /// Pre-commit leaves.
    pub members: Vec<Member>,
}

/// The staged-commit policy (module docs).
pub fn validate_commit(gv: &GroupView, sender: &Sender, sc: &StagedCommit) -> Res<CommitVerdict> {
    let rej = |m: String| e(Code::CommitRejected, m);
    let Sender::Member(idx) = sender else { return Err(rej(format!("commit from non-member sender {sender:?} (external commit) rejected"))) };
    let desc = Descriptor::parse(gv.descriptor)?;
    let chain = gv.chain;
    let v = check_gc(sc.group_context(), gv.descriptor, chain, gv.roster_version, Code::CommitRejected)?;
    let p = chain.version(v).unwrap();
    let adm = admitted(&desc, p);
    let adm_head = admitted(&desc, chain.head());
    let members = &gv.members;
    let me_leaf = members.iter().find(|m| m.index == *idx).ok_or_else(|| rej("committer leaf not in tree".into()))?;
    let (committer, centry) = leaf_adm(&adm, &me_leaf.credential, &me_leaf.signature_key)
        .ok_or_else(|| rej(format!("committer {} is not an admissible leaf of this {} group in roster v{v}", name(&me_leaf.credential), desc.kind.as_str())))?;
    let admin_scope = desc.kind.admin_scope();
    let mut removed = BTreeSet::new();
    let mut added = vec![];
    for qp in sc.queued_proposals() {
        match qp.proposal() {
            Proposal::Add(a) => {
                ensure!(!admin_scope || centry.role.is_admin(), Code::CommitRejected, "Add by non-admin {} in a {} group", hex(&committer), desc.kind.as_str());
                let ln = a.key_package().leaf_node();
                let (dev, de) = leaf_adm(&adm, ln.credential(), ln.signature_key().as_slice())
                    .ok_or_else(|| rej(format!("Add of {} which is not an admissible device (key) of this group", name(ln.credential()))))?;
                ensure!(!(desc.kind == GroupType::Pair && gv.frozen.contains(&hex(&de.member))), Code::CommitRejected, "Add of a device of a frozen pair peer (N9)");
                let kp_ref = a.key_package().hash_ref(&openmls_rust_crypto::RustCrypto::default()).map_err(mls)?;
                added.push((dev, kp_ref.as_slice().to_vec()));
            }
            Proposal::Remove(r) => {
                let t = members.iter().find(|m| m.index == r.removed()).ok_or_else(|| rej("Remove of unknown leaf".into()))?;
                // R2-12/R5-02/SD-15: only a leaf the verified roster no longer admits — judged at the
                // receiver's verified head (>= V), so a member can drop its revoked device without the
                // admin-only GroupContextExtensions; a receiver behind the head rejects until it syncs.
                ensure!(leaf_adm(&adm_head, &t.credential, &t.signature_key).is_none(),
                    Code::CommitRejected, "Remove of {} which is still admissible (active in the verified roster)", name(&t.credential));
                removed.insert(r.removed());
            }
            Proposal::Update(u) => {
                let Sender::Member(ps) = qp.sender() else { return Err(rej("Update from non-member".into())) };
                let cur = members.iter().find(|m| m.index == *ps).ok_or_else(|| rej("Update of unknown leaf".into()))?;
                let ln = u.leaf_node();
                ensure!(ln.credential() == &cur.credential && leaf_adm(&adm, ln.credential(), ln.signature_key().as_slice()).is_some(),
                    Code::CommitRejected, "Update with changed credential or key not in roster");
            }
            Proposal::GroupContextExtensions(_) => {
                ensure!(!admin_scope || centry.role.is_admin(), Code::CommitRejected, "GroupContextExtensions by non-admin in a {} group", desc.kind.as_str());
            }
            other => return Err(rej(format!("proposal type {:?} not allowed", other.proposal_type()))),
        }
    }
    let has_path = sc.update_path_leaf_node().is_some();
    if let Some(ln) = sc.update_path_leaf_node() {
        ensure!(ln.credential() == &me_leaf.credential && leaf_adm(&adm, ln.credential(), ln.signature_key().as_slice()).is_some(),
            Code::CommitRejected, "UpdatePath of {} changes credential or uses a key not in roster ({})", hex(&committer), name(ln.credential()));
    }
    // R2-12 invariant: every leaf after the commit is admissible (Adds and path checked above).
    if let Some(m) = members.iter().filter(|m| !removed.contains(&m.index)).find(|m| leaf_adm(&adm, &m.credential, &m.signature_key).is_none()) {
        return Err(rej(format!("after commit the tree would keep {} which is not admissible in roster v{v}", name(&m.credential))));
    }
    Ok(CommitVerdict { version: v, committer, has_path, added })
}

// ---------------- device & key packages ----------------

pub fn device_init(state: &mut State, p: &Provider, device_id: Id16, member_id: Id16) -> Res<J> {
    ensure!(state.device.is_none(), Code::InvalidInput, "device already initialised");
    let sk = util::random::<32>();
    let signer = SignatureKeyPair::new(CS.signature_algorithm()).map_err(mls)?;
    signer.store(p.storage()).map_err(mls)?;
    state.device = Some(crate::state::DeviceIdentity { device_id, member_id, identity_sk: sk, mls_public: signer.to_public_vec() });
    Ok(device_info(state)?)
}

pub fn device_info(state: &State) -> Res<J> {
    let d = state.device()?;
    Ok(json!({"deviceId": hex(&d.device_id), "memberId": hex(&d.member_id), "identityKey": hex(&d.identity_public()),
        "mlsSignatureKey": hex(&d.mls_public), "identityFingerprint": hex(&util::sha256(&[&d.identity_public()]))}))
}

pub fn key_package(state: &State, p: &Provider, lifetime: u64, last_resort: bool) -> Res<Vec<u8>> {
    let mut b = KeyPackage::builder().leaf_node_capabilities(caps()).key_package_lifetime(Lifetime::new(lifetime));
    if last_resort { b = b.mark_as_last_resort(); }
    let kpb = b.build(CS, p, &signer(state, p)?, credential(state)?).map_err(mls)?;
    kpb.key_package().tls_serialize_detached().map_err(mls)
}

/// SD-01/SD-11: a KeyPackage is admissible for Add iff its signature verifies, its credential +
/// key match an admissible device, its lifetime is <= 30 days and covers `now`, and it is not a
/// last-resort KP unless explicitly allowed.
pub fn check_key_package(bytes: &[u8], adm: &BTreeMap<Id16, Adm>, now: u64, allow_last_resort: bool) -> Res<(KeyPackage, Id16, Id16, bool)> {
    let rej = |m: String| e(Code::AddRejected, m);
    let kp_in = KeyPackageIn::tls_deserialize_exact(bytes).map_err(|x| rej(format!("malformed KeyPackage: {x:?}")))?;
    let kp = kp_in.validate(&openmls_rust_crypto::RustCrypto::default(), ProtocolVersion::Mls10).map_err(|x| rej(format!("invalid KeyPackage: {x:?}")))?;
    let ln = kp.leaf_node();
    let (dev, a) = leaf_adm(adm, ln.credential(), ln.signature_key().as_slice())
        .ok_or_else(|| rej(format!("KeyPackage of {} does not match an admissible roster device/key", name(ln.credential()))))?;
    let lt = kp.life_time();
    ensure!(lt.not_after().saturating_sub(lt.not_before()) <= KP_MAX_LIFETIME, Code::AddRejected, "KeyPackage lifetime exceeds 30 days");
    ensure!(lt.not_before() <= now && now <= lt.not_after(), Code::AddRejected, "KeyPackage expired or not yet valid");
    let lr = kp.last_resort();
    ensure!(!lr || allow_last_resort, Code::AddRejected, "last-resort KeyPackage refused (only when no regular KP is available, with mandatory UpdatePath)");
    Ok((kp, dev, a.member, lr))
}

// ---------------- group lifecycle ----------------

fn pair_key(d: &Descriptor) -> Option<String> {
    (d.kind == GroupType::Pair).then(|| format!("{}:{}{}", hex(&d.team_id), hex(&d.members[0]), hex(&d.members[1])))
}

/// N2a: close every older generation of the same pair; refuse if an equal/newer one exists.
fn supersede_pairs(state: &mut State, d: &Descriptor, gid: &H32, code: Code) -> Res<()> {
    let Some(k) = pair_key(d) else { return Ok(()) };
    for g in state.groups.values() {
        if g.group_id == *gid { continue; }
        if let Ok(od) = Descriptor::parse(&g.descriptor) {
            if pair_key(&od).as_deref() == Some(&k) && od.generation >= d.generation {
                return err(if code == Code::WelcomeRejected { Code::ClosedGeneration } else { code },
                    format!("pair generation {} superseded by existing generation {}", d.generation, od.generation));
            }
        }
    }
    for g in state.groups.values_mut() {
        if let Ok(od) = Descriptor::parse(&g.descriptor) {
            if pair_key(&od).as_deref() == Some(&k) && od.generation < d.generation { g.closed = true; }
        }
    }
    Ok(())
}

pub fn create_group(state: &mut State, p: &Provider, desc: Descriptor) -> Res<J> {
    desc.validate()?;
    let d = state.device()?.clone();
    let team = state.team(&desc.team_id)?;
    ensure!(!team.chain.frozen(), Code::Equivocation, "team frozen");
    let head = team.chain.head();
    let adm = admitted(&desc, head);
    let me = adm.get(&d.device_id).ok_or_else(|| e(Code::InvalidInput, "this device is not admissible in the group it creates"))?;
    match desc.kind {
        GroupType::Team | GroupType::Project | GroupType::Admin => ensure!(me.role.is_admin(), Code::InvalidInput, "team/project/admin groups are created by an admin"),
        GroupType::Pair => {
            let peer = desc.peer_of(&d.member_id).ok_or_else(|| e(Code::InvalidInput, "pair must include this member"))?;
            ensure!(head.member(&peer).is_some(), Code::InvalidInput, "pair peer is not in the roster");
            ensure!(!team.frozen_peers.contains(&hex(&peer)), Code::Frozen, "pair with a frozen peer (N9)");
        }
        GroupType::Personal => {}
    }
    let gid = desc.group_id();
    ensure!(!state.groups.contains_key(&hex(&gid)) && !state.seen_epochs.contains_key(&hex(&gid)), Code::InvalidInput, "group already exists");
    let head_hash = team.chain.hash_of(head.version).unwrap();
    let version = head.version;
    supersede_pairs(state, &desc, &gid, Code::ClosedGeneration)?;
    let canon = desc.canonical();
    let cfg = MlsGroupCreateConfig::builder()
        .ciphersuite(CS)
        .use_ratchet_tree_extension(true)
        .wire_format_policy(WIRE_POLICY)
        .capabilities(caps())
        .with_group_context_extensions(gc_extensions(&head_hash, &canon)?)
        .build();
    MlsGroup::new_with_group_id(p, &signer(state, p)?, &cfg, GroupId::from_slice(&gid), credential(state)?).map_err(mls)?;
    state.groups.insert(hex(&gid), GroupMeta { group_id: gid, team_id: desc.team_id, descriptor: canon, kind: desc.kind,
        roster_version: version, closed: false, evicted: false, weak_fs: BTreeSet::new(), track: Default::default() });
    state.seen_epochs.insert(hex(&gid), 0);
    Ok(json!({"groupId": hex(&gid), "epoch": 0, "rosterVersion": version}))
}

fn usable_group<'a>(state: &'a State, gid: &H32) -> Res<(&'a GroupMeta, &'a TeamState)> {
    let meta = state.group(gid)?;
    ensure!(!meta.closed, Code::ClosedGeneration, "group belongs to a closed pair generation");
    ensure!(!meta.evicted, Code::Mls, "this device was removed from the group");
    let team = state.team(&meta.team_id)?;
    ensure!(!team.chain.frozen(), Code::Equivocation, "team frozen (roster equivocation)");
    Ok((meta, team))
}

pub struct CommitSpec {
    pub adds: Vec<KeyPackage>,
    pub removes: Vec<LeafNodeIndex>,
    pub weak: Vec<Id16>,
}

/// Build, self-validate (same policy as receivers), and merge a commit. The roster hash moves to
/// the verified head when the group is behind it.
pub fn commit(state: &mut State, p: &Provider, gid: &H32, spec: CommitSpec, reject_code: Code) -> Res<J> {
    let (meta, team) = usable_group(state, gid)?;
    let head_v = team.chain.head_version();
    let mut group = load_group(p, gid)?;
    let signer = signer(state, p)?;
    let mut b = group.commit_builder().force_self_update(true).consume_proposal_store(false)
        .propose_adds(spec.adds).propose_removals(spec.removes);
    let me_admin = team.chain.head().active_device(&state.device()?.device_id).is_some_and(|(m, _)| m.role.is_admin());
    let may_gce = !meta.kind.admin_scope() || me_admin;
    if meta.roster_version < head_v && may_gce {
        b = b.propose_group_context_extensions(gc_extensions(&team.chain.hash_of(head_v).unwrap(), &meta.descriptor)?).map_err(mls)?;
    }
    let bundle = b.load_psks(p.storage()).map_err(mls)?
        .build(p.rand(), p.crypto(), &signer, |_| true).map_err(mls)?
        .stage_commit(p).map_err(mls)?;
    let own = Sender::Member(group.own_leaf_index());
    let gv = GroupView { descriptor: &meta.descriptor, roster_version: meta.roster_version, chain: &team.chain, frozen: &team.frozen_peers, members: group.members().collect() };
    let verdict = match validate_commit(&gv, &own, group.pending_commit().unwrap()) {
        Ok(v) => v,
        Err(x) => return Err(e(if x.code == Code::CommitRejected { reject_code } else { x.code }, x.msg)),
    };
    group.merge_pending_commit(p).map_err(mls)?;
    let epoch = group.epoch().as_u64();
    let meta = state.group_mut(gid)?;
    meta.roster_version = verdict.version;
    meta.weak_fs.remove(&hex(&verdict.committer));
    for w in spec.weak { meta.weak_fs.insert(hex(&w)); }
    state.seen_epochs.insert(hex(gid), epoch);
    Ok(json!({
        "commit": hex(&bundle.commit().to_bytes().map_err(mls)?),
        "welcome": bundle.welcome().map(|w| MlsMessageOut::from_welcome(w.clone(), ProtocolVersion::Mls10).to_bytes()).transpose().map_err(mls)?.map(|b| hex(&b)),
        "epoch": epoch, "rosterVersion": verdict.version,
    }))
}

pub fn add(state: &mut State, p: &Provider, gid: &H32, kps: &[Vec<u8>], now: u64, allow_last_resort: bool) -> Res<J> {
    let (meta, team) = usable_group(state, gid)?;
    let desc = Descriptor::parse(&meta.descriptor)?;
    let head = team.chain.head();
    let adm = admitted(&desc, head);
    let d = state.device()?;
    let me = adm.get(&d.device_id).ok_or_else(|| e(Code::AddRejected, "this device is not admissible in the group"))?;
    ensure!(!desc.kind.admin_scope() || me.role.is_admin(), Code::AddRejected, "only admins add to team/project/admin groups");
    let group = load_group(p, gid)?;
    let mut out = vec![];
    let mut weak = vec![];
    for b in kps {
        let (kp, dev, member, lr) = check_key_package(b, &adm, now, allow_last_resort)?;
        ensure!(!(desc.kind == GroupType::Pair && team.frozen_peers.contains(&hex(&member))), Code::AddRejected, "device of a frozen pair peer (N9)");
        ensure!(group.members().all(|m| leaf_id(&m.credential) != Some(dev)), Code::AddRejected, "device already in the group");
        if lr { weak.push(dev); }
        out.push(kp);
    }
    commit(state, p, gid, CommitSpec { adds: out, removes: vec![], weak }, Code::AddRejected)
}

pub fn remove(state: &mut State, p: &Provider, gid: &H32, devices: &[Id16]) -> Res<J> {
    usable_group(state, gid)?;
    let group = load_group(p, gid)?;
    let mut leaves = vec![];
    for dv in devices {
        let m = group.members().find(|m| leaf_id(&m.credential) == Some(*dv)).ok_or_else(|| e(Code::NotFound, format!("device {} not in group", hex(dv))))?;
        leaves.push(m.index);
    }
    commit(state, p, gid, CommitSpec { adds: vec![], removes: leaves, weak: vec![] }, Code::CommitRejected)
}

/// Remove every leaf that is no longer admissible at the roster head (rotation after a removal).
pub fn rotate(state: &mut State, p: &Provider, gid: &H32) -> Res<J> {
    let (meta, team) = usable_group(state, gid)?;
    let desc = Descriptor::parse(&meta.descriptor)?;
    let adm = admitted(&desc, team.chain.head());
    let group = load_group(p, gid)?;
    let leaves: Vec<LeafNodeIndex> = group.members().filter(|m| leaf_adm(&adm, &m.credential, &m.signature_key).is_none()).map(|m| m.index).collect();
    commit(state, p, gid, CommitSpec { adds: vec![], removes: leaves, weak: vec![] }, Code::CommitRejected)
}

/// N2: the loser of a simultaneous pair creation (the server answered `409` to its first commit)
/// drops its own never-shared group so it can accept the winner's Welcome for the same groupId.
/// Only a group at epoch 0 whose single leaf is this device can be discarded; `seen_epochs` keeps
/// its entry, so a Welcome must still be for a newer epoch (SD-11).
pub fn discard(state: &mut State, p: &Provider, gid: &H32) -> Res<J> {
    state.group(gid)?;
    let mut group = load_group(p, gid)?;
    ensure!(group.epoch().as_u64() == 0 && group.members().count() == 1, Code::InvalidInput,
        "only an own, never-shared group (epoch 0, single leaf) can be discarded");
    group.delete(p.storage()).map_err(mls)?;
    state.groups.remove(&hex(gid));
    Ok(json!({"groupId": hex(gid), "discarded": true}))
}

/// Accept a Welcome under §3.1 rules (team/project: R2-01 inviter/admin at >= rosterHead, pending
/// join; pair/personal: N10 sender is a descriptor member's active, unfrozen leaf; all: groupId =
/// H(descriptor), recipient and every leaf admissible, epoch newer than anything seen).
pub fn join(state: &mut State, p: &Provider, welcome: &[u8]) -> Res<J> {
    let rej = |m: String| e(Code::WelcomeRejected, m);
    let d = state.device()?.clone();
    let msg = MlsMessageIn::tls_deserialize_exact(welcome).map_err(|x| rej(format!("malformed: {x:?}")))?;
    let MlsMessageBodyIn::Welcome(w) = msg.extract() else { return Err(rej("not a Welcome".into())) };
    let staged = StagedWelcome::new_from_welcome(p, &join_config(), w, None).map_err(|x| rej(format!("{x:?}")))?;
    let gc = staged.group_context().clone();
    let dbytes = gc.extensions().unknown(DESC_EXT).ok_or_else(|| rej("group descriptor extension missing".into()))?.0.clone();
    let desc = Descriptor::parse(&dbytes).map_err(|x| rej(x.msg))?;
    let gid = desc.group_id();
    ensure!(gc.group_id().as_slice() == gid.as_slice(), Code::WelcomeRejected, "groupId != SHA-256(descriptor)");
    let team = state.team(&desc.team_id).map_err(|_| rej("Welcome for a team whose genesis was not obtained out of band (unknown teamId)".into()))?;
    ensure!(!team.chain.frozen(), Code::Equivocation, "team frozen");
    let v = check_gc(&gc, &dbytes, &team.chain, team.floor_version, Code::WelcomeRejected)?;
    let pv = team.chain.version(v).unwrap();
    let adm = admitted(&desc, pv);
    ensure!(adm.contains_key(&d.device_id), Code::WelcomeRejected, "this device is not admissible in the group (roster v{v})");
    if let Some(m) = staged.members().find(|m| leaf_adm(&adm, &m.credential, &m.signature_key).is_none()) {
        return Err(rej(format!("Welcome tree contains {} which is not admissible in roster v{v}", name(&m.credential))));
    }
    let ws = staged.welcome_sender().map_err(mls)?;
    let (sender, sentry) = leaf_adm(&adm, ws.credential(), ws.signature_key().as_slice())
        .ok_or_else(|| rej(format!("Welcome sender {} is not an admissible leaf", name(ws.credential()))))?;
    match desc.kind {
        GroupType::Team | GroupType::Project | GroupType::Admin => {
            if desc.kind == GroupType::Team {
                ensure!(team.join.is_some() || team.joined, Code::WelcomeRejected, "no pending JoinRequest/pairing for this team");
            } else {
                ensure!(team.joined || team.join.is_some(), Code::WelcomeRejected, "project Welcome without being (or joining) in the team");
            }
            // R2-05: the admin group carries invite secrets — a Welcome into it only from an admin, never the inviter.
            let inviter = desc.kind != GroupType::Admin
                && team.join.as_ref().is_some_and(|j| team.chain.cert(&sender).is_some_and(|c| util::sha256(&[&c.identity_key]) == j.inviter_fingerprint));
            ensure!(sentry.role.is_admin() || inviter, Code::WelcomeRejected,
                "Welcome from {} who is neither the inviter nor an admin in roster v{v} (>= rosterHead)", hex(&sender));
        }
        GroupType::Pair | GroupType::Personal => {
            ensure!(desc.named_members().contains(&sentry.member), Code::WelcomeRejected, "Welcome sender is not a member of the group descriptor (N10)");
            ensure!(!team.frozen_peers.contains(&hex(&sentry.member)), Code::WelcomeRejected, "Welcome from a frozen pair peer (N9/N10)");
        }
    }
    let epoch = gc.epoch().as_u64();
    ensure!(!state.groups.contains_key(&hex(&gid)), Code::WelcomeRejected,
        "a local group with this groupId exists (a losing own pair creation is discarded first, N2)");
    if let Some(seen) = state.seen_epochs.get(&hex(&gid)) {
        ensure!(epoch > *seen, Code::WelcomeRejected, "replayed/old Welcome: epoch {epoch} <= known {seen}");
    }
    supersede_pairs(state, &desc, &gid, Code::WelcomeRejected)?;
    staged.into_group(p).map_err(mls)?;
    state.groups.insert(hex(&gid), GroupMeta { group_id: gid, team_id: desc.team_id, descriptor: dbytes, kind: desc.kind,
        roster_version: v, closed: false, evicted: false, weak_fs: BTreeSet::new(), track: Default::default() });
    state.seen_epochs.insert(hex(&gid), epoch);
    if desc.kind == GroupType::Team {
        let t = state.team_mut(&desc.team_id)?;
        t.joined = true;
        t.join = None;
    }
    Ok(json!({"groupId": hex(&gid), "epoch": epoch, "rosterVersion": v, "type": desc.kind.as_str(), "sender": hex(&sender)}))
}

// ---------------- messages ----------------

/// SD-06 authenticated data: `rosterVersion u64 ‖ rosterHash[32] ‖ epoch u64 ‖ epochAuthenticator`.
fn aad(version: u64, hash: &H32, epoch: u64, auth: &[u8]) -> Vec<u8> {
    let mut a = version.to_be_bytes().to_vec();
    a.extend_from_slice(hash);
    a.extend_from_slice(&epoch.to_be_bytes());
    a.extend_from_slice(auth);
    a
}

pub struct OpSpec {
    pub op: OpType,
    pub author: Option<Id16>,
    pub item_id: Id16,
    pub rev: u64,
    pub recipient: Option<Id16>,
    pub payload: crate::cbor::Value,
    pub op_id: Id16,
    /// Explicit seq (a number reserved from the member-wide counter); default head + 1.
    pub seq: Option<u64>,
}

/// Sign `spec` as this device under the group's current epoch / roster version.
pub fn build_op(state: &State, p: &Provider, gid: &H32, spec: &OpSpec) -> Res<(SignedOp, Vec<u8>)> {
    let (meta, team) = usable_group(state, gid)?;
    let desc = Descriptor::parse(&meta.descriptor)?;
    ensure!(!team.removed, Code::Frozen, "removed from team");
    if let Some(peer) = state.device().ok().and_then(|d| desc.peer_of(&d.member_id)) {
        ensure!(!team.frozen_peers.contains(&hex(&peer)), Code::Frozen, "pair frozen until the peer's safety number is verified (N9)");
    }
    ensure!(tree_state(state, p, gid)? != 2, Code::Frozen, "hold: a leaf awaits rotation (roster revoked it); content stays in outbox");
    let d = state.device()?;
    let group = load_group(p, gid)?;
    let epoch = group.epoch().as_u64();
    let v = meta.roster_version;
    let my = meta.track.seq.get(&hex(&d.member_id)).map(|t| t.max_known).unwrap_or(0);
    let seq = if spec.op.is_control() { 0 } else { spec.seq.unwrap_or(my + 1) };
    let mut payload = spec.payload.clone();
    if spec.op == OpType::SeqHead { payload = crate::cbor::MapB::new().put("lastSeq", crate::cbor::Value::U(my)).build(); }
    let header = Header {
        team_id: meta.team_id, group_descriptor_hash: *gid, epoch, roster_version: v, roster_hash: team.chain.hash_of(v).unwrap(),
        author_member_id: spec.author.unwrap_or(d.member_id), signer_member_id: d.member_id, signer_device_id: d.device_id,
        op_type: spec.op as u8, item_id: spec.item_id, rev: spec.rev, seq, recipient_member_id: spec.recipient.unwrap_or(ZERO16),
        seq_head: seq.max(my),
    };
    let mut op = SignedOp { op_id: spec.op_id, header, payload, sig: [0; 64] };
    op.sig = util::ed25519_sign(&d.identity_sk, &op.signed_bytes());
    let raw = op.to_bytes();
    ensure!(raw.len() <= crate::itemop::MAX_OP_BYTES, Code::OpRejected, "operation larger than 64 KiB (use attachments)");
    Ok((op, raw))
}

/// Build, sign and apply one own op locally (outbox enqueue: also offline). Returns the op, its bytes and the outcome.
pub fn apply_own(state: &mut State, p: &Provider, db: &Db, gid: &H32, spec: &OpSpec, now: u64) -> Res<(SignedOp, Vec<u8>, J)> {
    let (op, raw) = build_op(state, p, gid, spec)?;
    let my = state.group(gid)?.track.seq.get(&hex(&op.header.signer_member_id)).map(|t| t.max_known).unwrap_or(0);
    ensure!(op.header.seq == 0 || op.header.seq > my, Code::InvalidInput, "seq {} not above own head {my}", op.header.seq);
    let me = state.device()?.device_id;
    let outcome = receive_op(state, db, gid, &op, &raw, &me, op.header.epoch, now)?;
    Ok((op, raw, outcome))
}

/// MLS-encrypt already signed op bytes in the group's current epoch (advances the sender ratchet).
pub fn encrypt(state: &State, p: &Provider, gid: &H32, op: &SignedOp, raw: &[u8]) -> Res<Vec<u8>> {
    let mut group = load_group(p, gid)?;
    ensure!(group.epoch().as_u64() == op.header.epoch, Code::Internal, "op signed for another epoch");
    let v = op.header.roster_version;
    let a = aad(v, &op.header.roster_hash, op.header.epoch, group.epoch_authenticator().as_slice());
    group.set_aad(a);
    let msg = group.create_message(p, &signer(state, p)?, raw).map_err(mls)?;
    msg.to_bytes().map_err(mls)
}

/// Verify + apply one decrypted op (also used for our own ops). Delivery receipts (§ store) dedup by
/// `(group, signer device, opId)`: the same bytes again are a duplicate; other bytes under a known opId are a
/// security event (rejected). A `seq` already applied from that device is a replay.
pub fn receive_op(state: &mut State, db: &Db, gid: &H32, op: &SignedOp, raw: &[u8], mls_sender: &Id16, epoch: u64, now: u64) -> Res<J> {
    let meta = state.group(gid)?;
    let desc = Descriptor::parse(&meta.descriptor)?;
    let team = state.team(&meta.team_id)?;
    let ctx = LiveCtx { chain: &team.chain, desc: &desc, group_id: gid, epoch, group_version: meta.roster_version, mls_sender };
    let at = replica::verify_live(&ctx, op)?.clone();
    let h = &op.header;
    let ct = util::sha256(&[raw]);
    if let Some((prev, _)) = db.seen_get(gid, &h.signer_device_id, &op.op_id)? {
        ensure!(util::ct_eq(&prev, &ct), Code::OpRejected, "security: replayed opId {} with other content", hex(&op.op_id));
        return Ok(json!("duplicate"));
    }
    ensure!(!db.seen_seq(gid, &h.signer_device_id, h.seq)?, Code::OpRejected, "replay: seq {} already applied from this device", h.seq);
    let ty = op.op_type()?;
    let payload = Payload::parse(ty, &op.payload)?;
    let home = replica::home_of(&desc);
    let head = team.chain.head().clone();
    let chain = team.chain.clone();
    let tid = meta.team_id;
    let me = state.device.as_ref().map(|d| d.device_id);
    let cx = ApplyCtx { chain: &chain, at: &at, head: &head, home: Some(home), group_kind: Some(desc.kind), desc: Some(&desc), me };
    let outcome = replica::Replica::new(db, tid).apply(&cx, op, raw)?;
    db.seen_put(gid, &tid, &h.signer_device_id, &op.op_id, h.seq, &ct, true, now)?;
    let meta = state.group_mut(gid)?;
    meta.track.record(op, &payload, |dv| chain.cert(dv).is_some_and(|c| c.member_id == op.header.signer_member_id), now);
    Ok(outcome.json())
}

/// Process one incoming MLS message for a known group.
pub fn process(state: &mut State, p: &Provider, db: &Db, gid: &H32, bytes: &[u8], now: u64) -> Res<J> {
    // Redelivery (ack lost after commit, cursor replay): the MLS secret is consumed, so dedup before decryption.
    let mh = util::sha256(&[bytes]);
    if db.msg_seen(&mh)? { return Ok(json!({"kind": "duplicate"})); }
    let r = process_inner(state, p, db, gid, bytes, now)?;
    db.msg_put(&mh, &state.group(gid).map(|g| g.team_id).unwrap_or([0; 32]), now)?;
    Ok(r)
}

fn process_inner(state: &mut State, p: &Provider, db: &Db, gid: &H32, bytes: &[u8], now: u64) -> Res<J> {
    let (meta, _) = usable_group(state, gid)?;
    let meta = meta.clone();
    let mut group = load_group(p, gid)?;
    let mi = MlsMessageIn::tls_deserialize_exact(bytes).map_err(mls)?;
    let pm = mi.try_into_protocol_message().map_err(mls)?;
    ensure!(pm.group_id().as_slice() == gid.as_slice(), Code::InvalidInput, "message for another group");
    // D-10: handshake travels as PublicMessage so the server can check it (public.rs); a commit hidden
    // in a PrivateMessage would bypass that barrier.
    ensure!(!(pm.is_handshake_message() && matches!(pm, ProtocolMessage::PrivateMessage(_))), Code::CommitRejected,
        "handshake message must be a PublicMessage");
    let processed = group.process_message(p, pm).map_err(mls)?;
    let sender = processed.sender().clone();
    let msg_epoch = processed.epoch().as_u64();
    let claimed = processed.aad().get(..8).map(|b| u64::from_be_bytes(b.try_into().unwrap()));
    let cred = processed.credential().clone();
    match processed.into_content() {
        ProcessedMessageContent::ApplicationMessage(app) => {
            let Sender::Member(_) = sender else { return err(Code::OpRejected, "application message from non-member") };
            let dev = leaf_id(&cred).ok_or_else(|| e(Code::OpRejected, "bad credential"))?;
            let raw = app.into_bytes();
            if let Some(r) = crate::names::try_open(state, gid, &raw) { return r; }
            if let Some(r) = crate::adminkeys::try_open(state, gid, &dev, &raw) { return r; }
            let op = SignedOp::parse(&raw)?;
            let outcome = receive_op(state, db, gid, &op, &raw, &dev, msg_epoch, now)?;
            let head = state.team(&meta.team_id)?.chain.head_version();
            Ok(json!({"kind": "op", "opType": op.op_type()?.name(), "opId": hex(&op.op_id), "author": hex(&op.header.author_member_id),
                "itemId": hex(&op.header.item_id), "signer": hex(&op.header.signer_member_id), "seq": op.header.seq, "outcome": outcome,
                "claimsNewerRoster": claimed.filter(|c| *c > head)}))
        }
        ProcessedMessageContent::StagedCommitMessage(sc) => {
            let team = state.team(&meta.team_id)?;
            let gv = GroupView { descriptor: &meta.descriptor, roster_version: meta.roster_version, chain: &team.chain, frozen: &team.frozen_peers, members: group.members().collect() };
            let verdict = validate_commit(&gv, &sender, &sc)?;
            let removed_self = sc.self_removed();
            group.merge_staged_commit(p, *sc).map_err(mls)?;
            let epoch = group.epoch().as_u64();
            let m = state.group_mut(gid)?;
            m.roster_version = verdict.version;
            if verdict.has_path { m.weak_fs.remove(&hex(&verdict.committer)); }
            if removed_self { m.evicted = true; }
            state.seen_epochs.insert(hex(gid), epoch);
            Ok(json!({"kind": if removed_self { "removed" } else { "commit" }, "epoch": epoch, "rosterVersion": verdict.version,
                "committer": hex(&verdict.committer), "added": verdict.added_json()}))
        }
        _ => err(Code::CommitRejected, "standalone proposals / external messages are not accepted"),
    }
}

/// R2-12 converged-state check against the roster head: 0 tree == admissible set, 1 admissible
/// devices not yet in the tree (pending Add), 2 a leaf the roster no longer admits (hold).
pub fn tree_state(state: &State, p: &Provider, gid: &H32) -> Res<i32> {
    let meta = state.group(gid)?;
    let team = state.team(&meta.team_id)?;
    let desc = Descriptor::parse(&meta.descriptor)?;
    let adm = admitted(&desc, team.chain.head());
    let group = load_group(p, gid)?;
    let leaves: Vec<Member> = group.members().collect();
    if leaves.iter().any(|m| leaf_adm(&adm, &m.credential, &m.signature_key).is_none()) { return Ok(2); }
    Ok(if leaves.len() < adm.len() { 1 } else { 0 })
}

pub fn group_info(state: &State, p: &Provider, gid: &H32) -> Res<J> {
    let meta = state.group(gid)?;
    let group = load_group(p, gid)?;
    let members: Vec<String> = group.members().map(|m| name(&m.credential)).collect();
    let desc = Descriptor::parse(&meta.descriptor)?;
    let seq: BTreeMap<String, J> = meta.track.seq.iter().map(|(m, t)| (m.clone(), json!({
        "base": t.base, "maxKnown": t.max_known, "lastHeard": t.last_heard, "lastOp": t.last_op,
        "gaps": t.gaps().iter().map(|(a, b, s)| json!({"from": a, "to": b, "since": s})).collect::<Vec<_>>()}))).collect();
    Ok(json!({"groupId": hex(gid), "epoch": group.epoch().as_u64(), "members": members, "rosterVersion": meta.roster_version,
        "descriptor": serde_json::to_value(&desc).unwrap(), "closed": meta.closed, "evicted": meta.evicted || !group.is_active(),
        "weakForwardSecrecy": meta.weak_fs.iter().collect::<Vec<_>>(), "treeState": tree_state(state, p, gid)?, "seq": seq}))
}

pub fn export_secret(p: &Provider, gid: &H32, label: &str, context: &[u8], len: usize) -> Res<Vec<u8>> {
    let group = load_group(p, gid)?;
    group.export_secret(p.crypto(), label, context, len).map_err(mls)
}

// ---------------- heartbeat (needs MLS exporter) ----------------

pub fn heartbeat_create(state: &State, p: &Provider, team_id: &H32, time: u64) -> Res<Vec<u8>> {
    let d = state.device()?;
    let team = state.team(team_id)?;
    let head = team.chain.head();
    ensure!(head.active_device(&d.device_id).is_some_and(|(m, _)| m.role.is_admin()), Code::InvalidInput, "heartbeats are published by owner/admin devices");
    let mut groups = vec![];
    for g in state.groups.values().filter(|g| g.team_id == *team_id && !g.closed && !g.evicted) {
        let mg = load_group(p, &g.group_id)?;
        let proof: H32 = mg.export_secret(p.crypto(), HB_EXPORTER_LABEL, &heartbeat::exporter_context(team_id, time), 32).map_err(mls)?.try_into().unwrap();
        groups.push(HbGroup { group_id: g.group_id.to_vec(), epoch: mg.epoch().as_u64(), epoch_proof: proof });
    }
    groups.sort_by(|a, b| a.group_id.cmp(&b.group_id));
    let hb = Heartbeat { team_id: *team_id, roster_version: head.version, roster_hash: team.chain.hash_of(head.version).unwrap(), groups, time, signer_device_id: d.device_id };
    let mut wire = hb.signed_bytes()?;
    let sig = util::ed25519_sign(&d.identity_sk, &wire);
    wire.extend_from_slice(&sig);
    Ok(wire)
}

/// R2-02 acceptance: signed by an owner/admin device of that version, version >= what we have
/// seen, and every epochProof for a group we hold at the same epoch matches our own exporter
/// (at least one must be checkable). Returns (counted, reason).
pub fn heartbeat_accept(state: &mut State, p: &Provider, wire: &[u8]) -> Res<J> {
    let (hb, signed, sig) = Heartbeat::parse(wire)?;
    let team = state.team(&hb.team_id)?;
    let chain = &team.chain;
    let not = |why: &str| Ok(json!({"counted": false, "reason": why}));
    if hb.roster_version > chain.head_version() { return err(Code::NeedRoster, format!("heartbeat at roster v{} beyond verified head", hb.roster_version)); }
    if chain.hash_of(hb.roster_version) != Some(hb.roster_hash) { return not("roster hash differs from our chain for that version (other branch)"); }
    if hb.roster_version < chain.head_version() { return not("heartbeat for an older roster version than we have seen"); }
    let pv = chain.version(hb.roster_version).unwrap();
    let Some((m, _)) = pv.active_device(&hb.signer_device_id) else { return not("signer not an active device") };
    if !m.role.is_admin() { return not("signer is not owner/admin"); }
    let Some(c) = chain.cert(&hb.signer_device_id) else { return not("no certificate") };
    if !util::ed25519_verify(&c.identity_key, &signed, &sig) { return not("bad signature"); }
    let mut checked = 0;
    for g in &hb.groups {
        let Ok(gid) = <H32>::try_from(g.group_id.as_slice()) else { continue };
        let Some(meta) = state.groups.get(&hex(&gid)) else { continue };
        if meta.evicted || meta.closed { continue; }
        let mg = load_group(p, &gid)?;
        if mg.epoch().as_u64() != g.epoch { continue; }
        let mine = mg.export_secret(p.crypto(), HB_EXPORTER_LABEL, &heartbeat::exporter_context(&hb.team_id, hb.time), 32).map_err(mls)?;
        if !util::ct_eq(&mine, &g.epoch_proof) { return not("epochProof mismatch (foreign branch/epoch)"); }
        checked += 1;
    }
    if checked == 0 { return not("no epochProof verifiable at our epochs"); }
    let rec = CountedHb { signer_member: m.member_id, signer_device: hb.signer_device_id, role: m.role, roster_version: hb.roster_version, time: hb.time };
    let team = state.team_mut(&hb.team_id)?;
    team.heartbeats.retain(|h| h.signer_device != rec.signer_device);
    team.heartbeats.push(rec);
    team.max_seen_version = team.max_seen_version.max(hb.roster_version);
    Ok(json!({"counted": true}))
}

/// "Confirmed" (§4.1): within `window`, a counted heartbeat from an owner device, or from
/// `effective_quorum` distinct admin memberIds — each still an active owner/admin at the head.
pub fn confirmation(team: &TeamState, now: u64, window: u64) -> J {
    let head = team.chain.head();
    let fresh: Vec<&CountedHb> = team.heartbeats.iter()
        .filter(|h| h.time + window >= now && h.time <= now + 300)
        .filter(|h| head.active_device(&h.signer_device).is_some_and(|(m, _)| m.role == h.role && m.role.is_admin()))
        .collect();
    let by_owner = fresh.iter().any(|h| h.role == Role::Owner);
    let admins: BTreeSet<Id16> = fresh.iter().filter(|h| h.role == Role::Admin).map(|h| h.signer_member).collect();
    let need = head.effective_quorum();
    json!({"confirmed": by_owner || admins.len() >= need, "byOwner": by_owner, "adminVotes": admins.len(), "required": need})
}
