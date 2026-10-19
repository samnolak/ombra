//! Signed roster chain (§4.1, SD-03/05, R2-01/03/07/10, N2b, D-3) — the single validator used by
//! clients and (in public-verification mode) by the server.
//!
//! Wire (canonical CBOR): `RosterUpdate { projection, signatures: [{deviceId, sig}], certs?: [cert],
//! ownership?: {offer, offerSig, acceptSig}, recoveryKeyChangeSig?, evidence?: [heartbeat] }`.
//! `sig = Ed25519(identityKey, "ombra/v1/roster" ‖ 0x00 ‖ canonical(projection))`,
//! `rosterHash = SHA-256(canonical(projection))`, `teamId = rosterHash(genesis)`.
//!
//! Invariants enforced on every accepted version N with predecessor P:
//! - `N.version = P.version + 1`, `N.prevHash = hash(P)`, `N.teamId = teamId` (genesis: version 1,
//!   no teamId/prevHash, exactly one member = owner, signed by the owner's self-issued device);
//! - exactly one member has role owner and it is `N.owner`; members/devices/access sorted, unique;
//! - every device added in N has a DeviceCertificate (hash = certHash, keys = pinned hashes,
//!   `addedInVersion = N.version`, serverId of this team) issued by a non-revoked device of the
//!   same member in P, or self-issued only when the member itself is new in N;
//! - device entries are immutable except for setting `revoked` once; a revoked device with
//!   `compromisedSince = s` forces revocation of every device it issued with `addedInVersion >= s`
//!   (cascade, R2-10); revoked/removed memberIds and deviceIds never come back (N2b);
//! - some single signer — a non-revoked device in P — has a role in P that permits every changed
//!   field (member: own devices/leave; admin: role-member members, access, pins; owner: admins,
//!   heartbeatQuorum, recovery with the old recovery key's co-signature);
//! - the owner changes only by (a) OwnershipOffer signed by the current owner + Accept signed by the
//!   recipient, or (b) the D-3 recovery flow: RecoveryProposal signed by the recovery key is
//!   published as `pending`, the current owner may veto (clear it) and after `notBefore` any member
//!   applies it with heartbeat evidence from the admin quorum dated >= notBefore. Admins can never
//!   change the owner by themselves;
//! - a different valid version with an already-known number is equivocation — but only when it is
//!   signed by someone with authority over it (an owner/admin of the parent version whose role
//!   permits every change, or a party of an owner change); then the chain freezes until an
//!   `EquivocationResolution` signed by an owner device of the parent picks the canonical branch.
//!   A conflicting version without such authority (e.g. a member's privateCommit/cause/own-device
//!   alternative) is rejected as `roster-conflict` and freezes nothing.

use std::collections::{BTreeMap, BTreeSet};

use serde::{Deserialize, Serialize};

use crate::cbor::{self, b, t, Fields, MapB, Value};
use crate::cert::DeviceCert;
use crate::ensure;
use crate::heartbeat::Heartbeat;
use crate::util::{self, e, err, hex, hexopt, hexser, hexvec, Code, Id16, Res, H32};

pub const ROSTER_DOMAIN: &[u8] = b"ombra/v1/roster";
pub const OFFER_DOMAIN: &[u8] = b"ombra/v1/ownership-offer";
pub const ACCEPT_DOMAIN: &[u8] = b"ombra/v1/ownership-accept";
pub const RECOVERY_PROPOSAL_DOMAIN: &[u8] = b"ombra/v1/recovery-proposal";
pub const RECOVERY_CHANGE_DOMAIN: &[u8] = b"ombra/v1/recovery-key-change";
pub const RESOLUTION_DOMAIN: &[u8] = b"ombra/v1/equivocation-resolution";
pub const MIN_TIME_LOCK: u64 = 24 * 3600;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Role { Member, Admin, Owner }

impl Role {
    pub fn as_str(self) -> &'static str {
        match self { Role::Member => "member", Role::Admin => "admin", Role::Owner => "owner" }
    }
    pub fn parse(s: &str) -> Res<Role> {
        match s { "member" => Ok(Role::Member), "admin" => Ok(Role::Admin), "owner" => Ok(Role::Owner),
            _ => err(Code::InvalidInput, format!("unknown role '{s}'")) }
    }
    pub fn is_admin(self) -> bool { self != Role::Member }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Level { Viewer, Contributor }

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Revocation { pub version: u64, pub compromised_since: u64 }

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceEntry {
    #[serde(with = "hexser")] pub device_id: Id16,
    #[serde(with = "hexser")] pub identity_key_hash: H32,
    #[serde(with = "hexser")] pub mls_key_hash: H32,
    #[serde(with = "hexser")] pub cert_hash: H32,
    pub added_in_version: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub revoked: Option<Revocation>,
}

impl DeviceEntry {
    pub fn active(&self) -> bool { self.revoked.is_none() }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MemberEntry {
    #[serde(with = "hexser")] pub member_id: Id16,
    pub role: Role,
    pub devices: Vec<DeviceEntry>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccessEntry {
    #[serde(with = "hexser")] pub project_id: Id16,
    #[serde(with = "hexser")] pub member_id: Id16,
    pub level: Level,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Recovery {
    #[serde(with = "hexser")] pub owner_recovery_key: [u8; 32],
    pub time_lock: u64,
    #[serde(default, with = "hexopt", skip_serializing_if = "Option::is_none")]
    pub custodian: Option<Id16>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecoveryProposal {
    #[serde(with = "hexser")] pub team_id: H32,
    pub base_version: u64,
    #[serde(with = "hexser")] pub base_prev_hash: H32,
    #[serde(with = "hexser")] pub new_owner: Id16,
    #[serde(with = "hexser")] pub new_owner_device: Id16,
    pub created_at: u64,
    pub not_before: u64,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Pending {
    pub proposal: RecoveryProposal,
    #[serde(with = "hexser")] pub sig: [u8; 64],
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Cause {
    /// "app" | "operator"
    pub kind: String,
    #[serde(with = "hexser")] pub audit_id: Id16,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Projection {
    #[serde(default, with = "hexopt", skip_serializing_if = "Option::is_none")]
    pub team_id: Option<H32>,
    pub version: u64,
    #[serde(default, with = "hexopt", skip_serializing_if = "Option::is_none")]
    pub prev_hash: Option<H32>,
    #[serde(with = "hexser")] pub owner: Id16,
    pub recovery: Recovery,
    pub heartbeat_quorum: u64,
    #[serde(with = "hexvec")] pub pins: Vec<H32>,
    pub members: Vec<MemberEntry>,
    pub access: Vec<AccessEntry>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pending: Option<Pending>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cause: Option<Cause>,
    #[serde(with = "hexser")] pub private_commit: H32,
}

// ---------------- CBOR mapping ----------------

fn level_str(l: Level) -> &'static str { match l { Level::Viewer => "viewer", Level::Contributor => "contributor" } }

impl RecoveryProposal {
    pub fn to_value(&self) -> Value {
        MapB::new().put("teamId", b(&self.team_id)).put("baseVersion", Value::U(self.base_version))
            .put("basePrevHash", b(&self.base_prev_hash)).put("newOwner", b(&self.new_owner))
            .put("newOwnerDevice", b(&self.new_owner_device)).put("createdAt", Value::U(self.created_at))
            .put("notBefore", Value::U(self.not_before)).build()
    }
    fn from_value(v: Value) -> Res<Self> {
        let mut f = Fields::new(v, "RecoveryProposal")?;
        let p = RecoveryProposal {
            team_id: f.req("teamId")?.fixed()?, base_version: f.req("baseVersion")?.u()?,
            base_prev_hash: f.req("basePrevHash")?.fixed()?, new_owner: f.req("newOwner")?.fixed()?,
            new_owner_device: f.req("newOwnerDevice")?.fixed()?, created_at: f.req("createdAt")?.u()?,
            not_before: f.req("notBefore")?.u()?,
        };
        f.finish()?;
        Ok(p)
    }
    pub fn signed_bytes(&self) -> Vec<u8> { domain(RECOVERY_PROPOSAL_DOMAIN, &cbor::encode(&self.to_value())) }
    pub fn hash(&self) -> H32 { util::sha256(&[&cbor::encode(&self.to_value())]) }
}

impl Recovery {
    pub fn to_value(&self) -> Value {
        MapB::new().put("ownerRecoveryKey", b(&self.owner_recovery_key)).put("timeLock", Value::U(self.time_lock))
            .put_opt("custodian", self.custodian.map(|c| b(&c))).build()
    }
    fn from_value(v: Value) -> Res<Self> {
        let mut f = Fields::new(v, "recovery")?;
        let r = Recovery {
            owner_recovery_key: f.req("ownerRecoveryKey")?.fixed()?, time_lock: f.req("timeLock")?.u()?,
            custodian: f.opt("custodian").map(|c| c.fixed()).transpose()?,
        };
        f.finish()?;
        Ok(r)
    }
}

impl Projection {
    pub fn to_value(&self) -> Value {
        let members = self.members.iter().map(|m| {
            let devices = m.devices.iter().map(|d| {
                MapB::new().put("deviceId", b(&d.device_id)).put("identityKeyHash", b(&d.identity_key_hash))
                    .put("mlsKeyHash", b(&d.mls_key_hash)).put("certHash", b(&d.cert_hash))
                    .put("addedInVersion", Value::U(d.added_in_version))
                    .put_opt("revoked", d.revoked.as_ref().map(|r| MapB::new().put("version", Value::U(r.version))
                        .put("compromisedSince", Value::U(r.compromised_since)).build()))
                    .build()
            }).collect();
            MapB::new().put("memberId", b(&m.member_id)).put("role", t(m.role.as_str())).put("devices", Value::A(devices)).build()
        }).collect();
        let access = self.access.iter().map(|a| MapB::new().put("projectId", b(&a.project_id))
            .put("memberId", b(&a.member_id)).put("level", t(level_str(a.level))).build()).collect();
        MapB::new()
            .put_opt("teamId", self.team_id.map(|x| b(&x)))
            .put("version", Value::U(self.version))
            .put_opt("prevHash", self.prev_hash.map(|x| b(&x)))
            .put("owner", b(&self.owner))
            .put("recovery", self.recovery.to_value())
            .put("heartbeatQuorum", Value::U(self.heartbeat_quorum))
            .put("pins", Value::A(self.pins.iter().map(|p| b(p)).collect()))
            .put("members", Value::A(members))
            .put("access", Value::A(access))
            .put_opt("pending", self.pending.as_ref().map(|p| MapB::new().put("proposal", p.proposal.to_value()).put("sig", b(&p.sig)).build()))
            .put_opt("cause", self.cause.as_ref().map(|c| MapB::new().put("kind", t(&c.kind)).put("auditId", b(&c.audit_id)).build()))
            .put("privateCommit", b(&self.private_commit))
            .build()
    }

    pub fn from_value(v: Value) -> Res<Self> {
        let mut f = Fields::new(v, "projection")?;
        let mut members = Vec::new();
        for m in f.req("members")?.array()? {
            let mut mf = Fields::new(m, "member")?;
            let mut devices = Vec::new();
            for d in mf.req("devices")?.array()? {
                let mut df = Fields::new(d, "device")?;
                let revoked = match df.opt("revoked") {
                    None => None,
                    Some(r) => {
                        let mut rf = Fields::new(r, "revoked")?;
                        let rv = Revocation { version: rf.req("version")?.u()?, compromised_since: rf.req("compromisedSince")?.u()? };
                        rf.finish()?;
                        Some(rv)
                    }
                };
                devices.push(DeviceEntry {
                    device_id: df.req("deviceId")?.fixed()?, identity_key_hash: df.req("identityKeyHash")?.fixed()?,
                    mls_key_hash: df.req("mlsKeyHash")?.fixed()?, cert_hash: df.req("certHash")?.fixed()?,
                    added_in_version: df.req("addedInVersion")?.u()?, revoked,
                });
                df.finish()?;
            }
            members.push(MemberEntry { member_id: mf.req("memberId")?.fixed()?, role: Role::parse(mf.req("role")?.text()?)?, devices });
            mf.finish()?;
        }
        let mut access = Vec::new();
        for a in f.req("access")?.array()? {
            let mut af = Fields::new(a, "access")?;
            let level = match af.req("level")?.text()? { "viewer" => Level::Viewer, "contributor" => Level::Contributor,
                x => return err(Code::InvalidInput, format!("unknown access level '{x}'")) };
            access.push(AccessEntry { project_id: af.req("projectId")?.fixed()?, member_id: af.req("memberId")?.fixed()?, level });
            af.finish()?;
        }
        let pending = match f.opt("pending") {
            None => None,
            Some(p) => {
                let mut pf = Fields::new(p, "pending")?;
                let pp = Pending { proposal: RecoveryProposal::from_value(pf.req("proposal")?)?, sig: pf.req("sig")?.fixed()? };
                pf.finish()?;
                Some(pp)
            }
        };
        let cause = match f.opt("cause") {
            None => None,
            Some(c) => {
                let mut cf = Fields::new(c, "cause")?;
                let cc = Cause { kind: cf.req("kind")?.text()?.to_string(), audit_id: cf.req("auditId")?.fixed()? };
                cf.finish()?;
                ensure!(cc.kind == "app" || cc.kind == "operator", Code::InvalidInput, "cause.kind must be app|operator");
                Some(cc)
            }
        };
        let p = Projection {
            team_id: f.opt("teamId").map(|x| x.fixed()).transpose()?,
            version: f.req("version")?.u()?,
            prev_hash: f.opt("prevHash").map(|x| x.fixed()).transpose()?,
            owner: f.req("owner")?.fixed()?,
            recovery: Recovery::from_value(f.req("recovery")?)?,
            heartbeat_quorum: f.req("heartbeatQuorum")?.u()?,
            pins: f.req("pins")?.array()?.iter().map(|p| p.fixed()).collect::<Result<_, _>>()?,
            members, access, pending, cause,
            private_commit: f.req("privateCommit")?.fixed()?,
        };
        f.finish()?;
        Ok(p)
    }

    pub fn canonical(&self) -> Vec<u8> { cbor::encode(&self.to_value()) }
    pub fn hash(&self) -> H32 { util::sha256(&[&self.canonical()]) }
    pub fn signed_bytes(&self) -> Vec<u8> { domain(ROSTER_DOMAIN, &self.canonical()) }

    pub fn member(&self, id: &Id16) -> Option<&MemberEntry> { self.members.iter().find(|m| &m.member_id == id) }
    pub fn role(&self, id: &Id16) -> Option<Role> { self.member(id).map(|m| m.role) }
    pub fn device(&self, id: &Id16) -> Option<(&MemberEntry, &DeviceEntry)> {
        self.members.iter().find_map(|m| m.devices.iter().find(|d| &d.device_id == id).map(|d| (m, d)))
    }
    /// Device listed and not revoked.
    pub fn active_device(&self, id: &Id16) -> Option<(&MemberEntry, &DeviceEntry)> {
        self.device(id).filter(|(_, d)| d.active())
    }
    pub fn access_level(&self, project: &Id16, member: &Id16) -> Option<Level> {
        self.access.iter().find(|a| &a.project_id == project && &a.member_id == member).map(|a| a.level)
    }
    pub fn admin_count(&self) -> usize { self.members.iter().filter(|m| m.role == Role::Admin).count() }
    /// `k_hb` actually required: the configured quorum, but never more than the admins that exist.
    pub fn effective_quorum(&self) -> usize {
        (self.heartbeat_quorum as usize).min(self.admin_count().max(1)).max(1)
    }
}

pub fn domain(d: &[u8], body: &[u8]) -> Vec<u8> {
    let mut m = Vec::with_capacity(d.len() + 1 + body.len());
    m.extend_from_slice(d);
    m.push(0);
    m.extend_from_slice(body);
    m
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Sig {
    #[serde(with = "hexser")] pub device_id: Id16,
    #[serde(with = "hexser")] pub sig: [u8; 64],
}

impl Sig {
    fn to_value(&self) -> Value { MapB::new().put("deviceId", b(&self.device_id)).put("sig", b(&self.sig)).build() }
    fn from_value(v: Value) -> Res<Self> {
        let mut f = Fields::new(v, "signature")?;
        let s = Sig { device_id: f.req("deviceId")?.fixed()?, sig: f.req("sig")?.fixed()? };
        f.finish()?;
        Ok(s)
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OwnershipOffer {
    #[serde(with = "hexser")] pub team_id: H32,
    #[serde(with = "hexser")] pub from: Id16,
    #[serde(with = "hexser")] pub to: Id16,
    pub roster_version: u64,
    #[serde(with = "hexser")] pub prev_hash: H32,
    pub new_role_of_from: Role,
    pub expires_at: u64,
}

impl OwnershipOffer {
    pub fn to_value(&self) -> Value {
        MapB::new().put("teamId", b(&self.team_id)).put("from", b(&self.from)).put("to", b(&self.to))
            .put("rosterVersion", Value::U(self.roster_version)).put("prevHash", b(&self.prev_hash))
            .put("newRoleOfFrom", t(self.new_role_of_from.as_str())).put("expiresAt", Value::U(self.expires_at)).build()
    }
    fn from_value(v: Value) -> Res<Self> {
        let mut f = Fields::new(v, "OwnershipOffer")?;
        let o = OwnershipOffer {
            team_id: f.req("teamId")?.fixed()?, from: f.req("from")?.fixed()?, to: f.req("to")?.fixed()?,
            roster_version: f.req("rosterVersion")?.u()?, prev_hash: f.req("prevHash")?.fixed()?,
            new_role_of_from: Role::parse(f.req("newRoleOfFrom")?.text()?)?, expires_at: f.req("expiresAt")?.u()?,
        };
        f.finish()?;
        Ok(o)
    }
    pub fn offer_bytes(&self) -> Vec<u8> { domain(OFFER_DOMAIN, &cbor::encode(&self.to_value())) }
    pub fn accept_bytes(&self) -> Vec<u8> { domain(ACCEPT_DOMAIN, &cbor::encode(&self.to_value())) }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Ownership { pub offer: OwnershipOffer, pub offer_sig: Sig, pub accept_sig: Sig }

/// The owner's decision on a recorded equivocation (§1 карантин п.3, R2-08): which of the two signed
/// versions with number `version` is canonical. Signed by an active owner device of version
/// `version - 1` (the common parent): `sig = Ed25519(identityKey, "ombra/v1/equivocation-resolution" ‖ 0x00 ‖
/// canonical({teamId, version, chosen, rejected}))`. Wire: canonical CBOR of those fields + `signature`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Resolution {
    #[serde(with = "hexser")] pub team_id: H32,
    pub version: u64,
    #[serde(with = "hexser")] pub chosen: H32,
    #[serde(with = "hexser")] pub rejected: H32,
    pub signature: Sig,
}

impl Resolution {
    fn body(&self) -> Value {
        MapB::new().put("teamId", b(&self.team_id)).put("version", Value::U(self.version))
            .put("chosen", b(&self.chosen)).put("rejected", b(&self.rejected)).build()
    }
    pub fn signed_bytes(&self) -> Vec<u8> { domain(RESOLUTION_DOMAIN, &cbor::encode(&self.body())) }
    pub fn to_bytes(&self) -> Vec<u8> {
        let Value::M(mut m) = self.body() else { unreachable!("body is a map") };
        m.push((t("signature"), self.signature.to_value()));
        cbor::encode(&Value::M(m))
    }
    pub fn parse(bytes: &[u8]) -> Res<Self> {
        let mut f = Fields::new(cbor::decode(bytes)?, "EquivocationResolution")?;
        let r = Resolution { team_id: f.req("teamId")?.fixed()?, version: f.req("version")?.u()?, chosen: f.req("chosen")?.fixed()?,
            rejected: f.req("rejected")?.fixed()?, signature: Sig::from_value(f.req("signature")?)? };
        f.finish()?;
        Ok(r)
    }
    /// Sign a decision between the version with hash `known` (in the signer's view of the chain) and the
    /// conflicting signed RosterUpdate `conflicting`, both with number `version`; `chosen` is "known" or
    /// "conflicting". Also used with the server's evidence when the signer's own chain never froze.
    pub fn sign(team_id: H32, version: u64, known: H32, conflicting: &[u8], chosen: &str, device: Id16, sk: &[u8; 32]) -> Res<Resolution> {
        let cp = RosterUpdate::parse(conflicting)?.projection;
        ensure!(cp.version == version, Code::InvalidInput, "conflicting update is v{}, not v{version}", cp.version);
        let conflicting = cp.hash();
        ensure!(conflicting != known, Code::InvalidInput, "the two versions are identical");
        let (chosen, rejected) = match chosen {
            "known" => (known, conflicting),
            "conflicting" => (conflicting, known),
            x => return err(Code::InvalidInput, format!("chosen must be 'known' or 'conflicting', got '{x}'")),
        };
        let mut r = Resolution { team_id, version, chosen, rejected, signature: Sig { device_id: device, sig: [0; 64] } };
        r.signature.sig = util::ed25519_sign(sk, &r.signed_bytes());
        Ok(r)
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RosterUpdate {
    pub projection: Projection,
    pub signatures: Vec<Sig>,
    pub certs: Vec<DeviceCert>,
    pub ownership: Option<Ownership>,
    pub recovery_key_change_sig: Option<[u8; 64]>,
    /// Heartbeat wire bytes proving time >= notBefore for a recovery apply.
    pub evidence: Vec<Vec<u8>>,
}

pub fn recovery_change_bytes(team_id: &H32, version: u64, rec: &Recovery) -> Vec<u8> {
    let mut body = team_id.to_vec();
    body.extend_from_slice(&version.to_be_bytes());
    body.extend(cbor::encode(&rec.to_value()));
    domain(RECOVERY_CHANGE_DOMAIN, &body)
}

impl RosterUpdate {
    pub fn to_value(&self) -> Value {
        MapB::new()
            .put("projection", self.projection.to_value())
            .put("signatures", Value::A(self.signatures.iter().map(Sig::to_value).collect()))
            .put_opt("certs", (!self.certs.is_empty()).then(|| Value::A(self.certs.iter().map(|c| b(&c.to_bytes())).collect())))
            .put_opt("ownership", self.ownership.as_ref().map(|o| MapB::new().put("offer", o.offer.to_value())
                .put("offerSig", o.offer_sig.to_value()).put("acceptSig", o.accept_sig.to_value()).build()))
            .put_opt("recoveryKeyChangeSig", self.recovery_key_change_sig.map(|s| b(&s)))
            .put_opt("evidence", (!self.evidence.is_empty()).then(|| Value::A(self.evidence.iter().map(|x| b(x)).collect())))
            .build()
    }
    pub fn to_bytes(&self) -> Vec<u8> { cbor::encode(&self.to_value()) }

    pub fn parse(bytes: &[u8]) -> Res<Self> {
        let mut f = Fields::new(cbor::decode(bytes)?, "RosterUpdate")?;
        let projection = Projection::from_value(f.req("projection")?)?;
        let signatures = f.req("signatures")?.array()?.into_iter().map(Sig::from_value).collect::<Res<Vec<_>>>()?;
        let certs = match f.opt("certs") {
            None => vec![],
            Some(c) => c.array()?.iter().map(|x| DeviceCert::parse(x.bytes()?)).collect::<Res<Vec<_>>>()?,
        };
        let ownership = match f.opt("ownership") {
            None => None,
            Some(o) => {
                let mut of = Fields::new(o, "ownership")?;
                let x = Ownership { offer: OwnershipOffer::from_value(of.req("offer")?)?,
                    offer_sig: Sig::from_value(of.req("offerSig")?)?, accept_sig: Sig::from_value(of.req("acceptSig")?)? };
                of.finish()?;
                Some(x)
            }
        };
        let recovery_key_change_sig = f.opt("recoveryKeyChangeSig").map(|s| s.fixed()).transpose()?;
        let evidence = match f.opt("evidence") {
            None => vec![],
            Some(v) => v.array()?.iter().map(|x| x.bytes().map(|b| b.to_vec())).collect::<Result<_, _>>()?,
        };
        f.finish()?;
        ensure!(signatures.len() <= 16 && certs.len() <= 256 && evidence.len() <= 64, Code::InvalidInput, "RosterUpdate too large");
        Ok(RosterUpdate { projection, signatures, certs, ownership, recovery_key_change_sig, evidence })
    }
}

// ---------------- chain ----------------

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VersionRec {
    #[serde(with = "hexser")] pub hash: H32,
    /// Full signed RosterUpdate wire bytes (servers re-serve them, clients can re-verify).
    #[serde(with = "hexser")] pub update: Vec<u8>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EquivocationRec {
    pub version: u64,
    #[serde(with = "hexser")] pub known: Vec<u8>,
    #[serde(with = "hexser")] pub conflicting: Vec<u8>,
}

/// An applied owner resolution: the rejected version can never be accepted (or re-freeze) again.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolutionRec {
    pub version: u64,
    #[serde(with = "hexser")] pub rejected: H32,
    /// Signed EquivocationResolution wire bytes (re-served so other clients can unfreeze).
    #[serde(with = "hexser")] pub resolution: Vec<u8>,
    /// N2b across a branch switch: ids that appeared only in the discarded branch stay "used" forever.
    #[serde(default)] pub discarded_members: BTreeSet<String>,
    #[serde(default)] pub discarded_devices: BTreeSet<String>,
}

/// Result of [`Chain::resolve`]: the head after the decision and whether the chain switched branch
/// (then every version ≥ `version` was replaced).
pub struct Resolved { pub switched: bool, pub head: u64, pub version: u64 }

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Chain {
    #[serde(with = "hexser")] pub team_id: H32,
    #[serde(with = "hexser")] pub server_id: Id16,
    pub versions: Vec<VersionRec>,
    /// Certificates by hex(deviceId), for every device that ever appeared.
    pub certs: BTreeMap<String, DeviceCert>,
    pub seen_members: BTreeSet<String>,
    pub seen_devices: BTreeSet<String>,
    #[serde(default)]
    pub equivocation: Option<EquivocationRec>,
    #[serde(default)]
    pub resolutions: Vec<ResolutionRec>,
    #[serde(skip)]
    parsed: Vec<Projection>,
}

pub enum Appended { New { version: u64, hash: H32 }, Known { version: u64 } }

impl Chain {
    /// Start a chain from a genesis RosterUpdate. `expected_team_id` is the genesis hash obtained
    /// out of band (invite link `t`, or own creation); `None` only for the server-side register,
    /// which merely records the claimed team.
    pub fn genesis(server_id: Id16, update_bytes: &[u8], expected_team_id: Option<H32>) -> Res<Chain> {
        let u = RosterUpdate::parse(update_bytes)?;
        let p = &u.projection;
        let hash = p.hash();
        if let Some(t) = expected_team_id {
            ensure!(t == hash, Code::RosterRejected, "genesis hash != expected teamId (team not created by the invite's owner)");
        }
        ensure!(p.version == 1 && p.team_id.is_none() && p.prev_hash.is_none(), Code::RosterRejected, "genesis must be version 1 without teamId/prevHash");
        ensure!(p.pending.is_none(), Code::RosterRejected, "genesis cannot carry a pending recovery");
        ensure!(p.members.len() == 1 && p.members[0].member_id == p.owner && p.members[0].role == Role::Owner,
            Code::RosterRejected, "genesis must contain exactly the owner");
        let mut chain = Chain { team_id: hash, server_id, versions: vec![], certs: BTreeMap::new(),
            seen_members: BTreeSet::new(), seen_devices: BTreeSet::new(), equivocation: None, resolutions: vec![], parsed: vec![] };
        check_invariants(p)?;
        let owner = &p.members[0];
        ensure!(!owner.devices.is_empty(), Code::RosterRejected, "owner has no device");
        for d in &owner.devices {
            let c = find_cert(&u, &d.device_id)?;
            check_new_device_cert(&chain, c, owner, d, 1)?;
            // Genesis devices: self-issued root or issued by another genesis device of the owner.
            let issuer = if c.self_root() { c } else {
                ensure!(owner.devices.iter().any(|x| x.device_id == c.issuer_device_id), Code::RosterRejected, "genesis cert issuer not an owner device");
                find_cert(&u, &c.issuer_device_id)?
            };
            ensure!(issuer.self_root() && c.verify(&issuer.identity_key), Code::RosterRejected, "genesis certificate signature invalid");
        }
        for c in &u.certs { chain.certs.insert(hex(&c.device_id), c.clone()); }
        // Signatures: at least one, all valid, by owner devices.
        ensure!(!u.signatures.is_empty(), Code::RosterRejected, "genesis unsigned");
        for s in &u.signatures {
            let (m, _) = p.active_device(&s.device_id).ok_or_else(|| e(Code::RosterRejected, "genesis signer not in genesis"))?;
            ensure!(m.role == Role::Owner, Code::RosterRejected, "genesis must be signed by the owner");
            let c = chain.certs.get(&hex(&s.device_id)).ok_or_else(|| e(Code::RosterRejected, "no cert for signer"))?;
            ensure!(util::ed25519_verify(&c.identity_key, &p.signed_bytes(), &s.sig), Code::BadSignature, "genesis signature invalid");
        }
        chain.record_seen(p);
        chain.versions.push(VersionRec { hash, update: update_bytes.to_vec() });
        chain.parsed.push(p.clone());
        Ok(chain)
    }

    /// Rebuild the parsed-projection cache after deserialization.
    pub fn rehydrate(&mut self) -> Res<()> {
        self.parsed = self.versions.iter().map(|v| RosterUpdate::parse(&v.update).map(|u| u.projection)).collect::<Res<_>>()?;
        Ok(())
    }

    /// The chain truncated to its first `len` versions, with seen-id sets and certificates
    /// re-derived from those versions only (used to judge a conflicting version fairly) — plus the ids
    /// of branches discarded by owner resolutions, which never become reusable (N2b).
    pub fn prefix(&self, len: usize) -> Res<Chain> {
        let mut c = Chain { team_id: self.team_id, server_id: self.server_id, versions: self.versions[..len].to_vec(),
            certs: BTreeMap::new(), seen_members: BTreeSet::new(), seen_devices: BTreeSet::new(), equivocation: None,
            resolutions: self.resolutions.clone(), parsed: self.parsed[..len].to_vec() };
        for v in &c.versions.clone() {
            let u = RosterUpdate::parse(&v.update)?;
            for x in &u.certs { c.certs.entry(hex(&x.device_id)).or_insert_with(|| x.clone()); }
            c.record_seen(&u.projection);
        }
        for r in &self.resolutions {
            c.seen_members.extend(r.discarded_members.iter().cloned());
            c.seen_devices.extend(r.discarded_devices.iter().cloned());
        }
        Ok(c)
    }

    pub fn head(&self) -> &Projection { self.parsed.last().expect("chain never empty") }
    pub fn head_version(&self) -> u64 { self.head().version }
    pub fn version(&self, v: u64) -> Option<&Projection> { v.checked_sub(1).and_then(|i| self.parsed.get(i as usize)) }
    pub fn hash_of(&self, v: u64) -> Option<H32> { v.checked_sub(1).and_then(|i| self.versions.get(i as usize)).map(|r| r.hash) }
    pub fn version_of_hash(&self, h: &[u8]) -> Option<u64> {
        self.versions.iter().position(|r| r.hash.as_slice() == h).map(|i| i as u64 + 1)
    }
    pub fn cert(&self, device: &Id16) -> Option<&DeviceCert> { self.certs.get(&hex(device)) }
    pub fn frozen(&self) -> bool { self.equivocation.is_some() }

    /// Safety number of a member (§2.1): iterated SHA-512 over the member's root identity key (its
    /// latest self-issued device in this chain — a recovered member gets a new root) and the
    /// identity keys of its current devices, shown as 60 digits in 12 groups. Returns (digits, root key).
    pub fn safety_number(&self, member: &Id16) -> Res<(String, [u8; 32])> {
        use sha2::{Digest, Sha512};
        let head = self.head();
        let m = head.member(member).ok_or_else(|| e(Code::NotFound, format!("member {} not in the head roster", hex(member))))?;
        let root = self.parsed.iter().filter_map(|p| p.member(member)).flat_map(|pm| pm.devices.iter())
            .filter_map(|d| self.cert(&d.device_id).filter(|c| c.self_root() && c.member_id == *member).map(|c| (d.added_in_version, c.identity_key)))
            .max_by_key(|(v, _)| *v).map(|(_, k)| k)
            .ok_or_else(|| e(Code::NotFound, "member has no root device certificate"))?;
        let mut keys: Vec<(Id16, [u8; 32])> = m.devices.iter().filter(|d| d.active())
            .filter_map(|d| self.cert(&d.device_id).map(|c| (d.device_id, c.identity_key))).collect();
        keys.sort();
        let mut h = Sha512::new();
        h.update(b"ombra/v1/safety-number\0");
        h.update(member);
        h.update(root);
        h.update([keys.len() as u8]);
        for (_, k) in &keys { h.update(k); }
        let mut digest = h.finalize();
        for _ in 0..5200 {
            let mut h = Sha512::new();
            h.update(digest);
            h.update(root);
            digest = h.finalize();
        }
        let groups: Vec<String> = (0..12).map(|i| {
            let c = &digest[i * 5..i * 5 + 5];
            let x = c.iter().fold(0u64, |acc, b| (acc << 8) | *b as u64);
            format!("{:05}", x % 100_000)
        }).collect();
        Ok((groups.join(" "), root))
    }

    /// R2-10: whether signatures of `device` at roster version `v` must be refused according to the
    /// verified chain — the latest version listing the device revoked it with `compromisedSince <= v`,
    /// or (cascade) its certificate issuer was already compromised when the device was added.
    /// A plain revocation (`compromisedSince` = revocation version) keeps earlier signatures valid.
    pub fn compromised_at(&self, device: &Id16, v: u64) -> bool {
        let (mut cur, mut at) = (*device, v);
        for _ in 0..=self.parsed.len() {
            let Some(d) = self.parsed.iter().rev().find_map(|p| p.device(&cur).map(|(_, d)| d)) else { return false };
            if d.revoked.as_ref().is_some_and(|r| r.compromised_since <= at) { return true; }
            let Some(c) = self.cert(&cur) else { return false };
            if c.self_root() { return false; }
            (cur, at) = (c.issuer_device_id, d.added_in_version);
        }
        true
    }

    fn record_seen(&mut self, p: &Projection) {
        for m in &p.members {
            self.seen_members.insert(hex(&m.member_id));
            for d in &m.devices { self.seen_devices.insert(hex(&d.device_id)); }
        }
    }

    /// Validate and append one RosterUpdate. On a valid conflicting version signed with authority over
    /// it (see [`Chain::authoritative`]) the chain records the equivocation (both signed versions) and
    /// returns `Code::Equivocation`; callers MUST persist that state change (the team stays frozen until
    /// [`Chain::resolve`]). A valid conflicting version without that authority, or one an owner
    /// resolution already rejected, is `Code::RosterConflict` and changes nothing.
    pub fn append(&mut self, update_bytes: &[u8], now: u64) -> Res<Appended> {
        ensure!(!self.frozen(), Code::Equivocation, "team frozen: roster equivocation recorded");
        let u = RosterUpdate::parse(update_bytes)?;
        let v = u.projection.version;
        let head = self.head_version();
        ensure!(v >= 2, Code::RosterRejected, "version must be >= 2");
        if v > head + 1 {
            return err(Code::NeedRoster, format!("gap: have v{head}, got v{v}"));
        }
        if v <= head {
            let known = &self.versions[(v - 1) as usize];
            let hash = u.projection.hash();
            if known.hash == hash { return Ok(Appended::Known { version: v }); }
            if self.resolutions.iter().any(|r| r.rejected == hash) {
                return err(Code::RosterConflict, format!("v{v} was rejected by the owner's equivocation resolution"));
            }
            // Different content for a known number: is it *validly signed* against the same parent?
            let prev = self.parsed[(v - 2) as usize].clone();
            let probe = self.prefix((v - 1) as usize)?;
            if probe.check_transition(&prev, &u, now).is_err() {
                return err(Code::RosterRejected, format!("conflicting v{v} is not validly signed"));
            }
            if !self.authoritative(&prev, &u) {
                return err(Code::RosterConflict, format!(
                    "conflicting v{v} is not signed by an owner/admin of v{} (still active as such at the head) with authority over its changes: rejected, team not frozen", v - 1));
            }
            self.equivocation = Some(EquivocationRec { version: v, known: known.update.clone(), conflicting: update_bytes.to_vec() });
            return err(Code::Equivocation, format!("two valid roster versions v{v}: team frozen until the owner resolves it"));
        }
        let prev = self.head().clone();
        self.check_transition(&prev, &u, now)?;
        for c in &u.certs { self.certs.entry(hex(&c.device_id)).or_insert_with(|| c.clone()); }
        self.record_seen(&u.projection);
        let hash = u.projection.hash();
        self.versions.push(VersionRec { hash, update: update_bytes.to_vec() });
        self.parsed.push(u.projection);
        Ok(Appended::New { version: v, hash })
    }

    /// Whether a conflicting version `u` that already passed `check_transition` from `p` is signed with
    /// authority over it, i.e. counts as equivocation. Evaluated on the full known chain: a signer must
    /// be an active, uncompromised device both in `p` and at the known head, and either (a) its role is
    /// owner/admin in `p` — permitting every change on its own — and still owner/admin at the head, or
    /// (b) for an owner change, it is the old or the new owner and still owner/admin at the head. Members
    /// (own devices, leave, privateCommit/cause) never have that authority, and neither has an admin or
    /// owner demoted or removed since: their alternatives are mere conflicts and must not let anyone
    /// freeze the team at will.
    fn authoritative(&self, p: &Projection, u: &RosterUpdate) -> bool {
        let n = &u.projection;
        let head = self.head();
        let Ok(changes) = diff(p, n) else { return false };
        let signed = n.signed_bytes();
        let signers: Vec<(Id16, Role, Role)> = u.signatures.iter().filter_map(|s| {
            let (m, _) = p.active_device(&s.device_id)?;
            let (hm, _) = head.active_device(&s.device_id)?;
            if self.compromised_at(&s.device_id, head.version) { return None; }
            let c = self.cert(&s.device_id)?;
            util::ed25519_verify(&c.identity_key, &signed, &s.sig).then_some((m.member_id, m.role, hm.role))
        }).collect();
        if changes.iter().any(|c| matches!(c, Change::Owner)) {
            // A party of the owner change that is still owner/admin at the head (a demoted ex-owner is not).
            return signers.iter().any(|(m, _, now)| (*m == p.owner || *m == n.owner) && now.is_admin());
        }
        signers.iter().any(|(m, r, now)| r.is_admin() && now.is_admin() && changes.iter().all(|c| self.permits(c, m, *r, p, n, u)))
    }

    /// Apply the owner's decision on the recorded equivocation (§1 карантин п.3, R2-08). The resolution
    /// must name exactly the two recorded versions and be signed by an active, uncompromised device of
    /// the owner at the head of the known chain; when it switches branch, that member must also be the
    /// owner at the new head (nobody "chooses" a branch that hands ownership away). Choosing the known
    /// version just unfreezes; choosing the conflicting one truncates the chain to the parent and appends
    /// it (re-validated), while every id seen only in the discarded branch stays used (N2b). Either way
    /// the rejected hash is remembered, so re-uploading it is a `roster-conflict`, never a new freeze.
    pub fn resolve(&mut self, bytes: &[u8], now: u64) -> Res<Resolved> {
        let q = self.equivocation.clone().ok_or_else(|| e(Code::InvalidInput, "no equivocation recorded: nothing to resolve"))?;
        let r = Resolution::parse(bytes)?;
        ensure!(r.team_id == self.team_id && r.version == q.version, Code::RosterRejected, "resolution is for another team or version");
        let known = self.hash_of(q.version).expect("equivocation version is in the chain");
        let conflicting = RosterUpdate::parse(&q.conflicting)?.projection.hash();
        ensure!((r.chosen == known && r.rejected == conflicting) || (r.chosen == conflicting && r.rejected == known),
            Code::RosterRejected, "resolution does not name the two recorded versions");
        let head = self.head();
        let (m, _) = head.active_device(&r.signature.device_id)
            .ok_or_else(|| e(Code::RosterRejected, format!("resolution signer is not an active device at the head v{}", head.version)))?;
        let resolver = m.member_id;
        ensure!(resolver == head.owner, Code::RosterRejected, "only the owner (at the head v{}) resolves an equivocation", head.version);
        ensure!(!self.compromised_at(&r.signature.device_id, head.version), Code::RosterRejected, "resolution signer device is compromised");
        let c = self.cert(&r.signature.device_id).ok_or_else(|| e(Code::RosterRejected, "resolution signer cert unknown"))?;
        ensure!(util::ed25519_verify(&c.identity_key, &r.signed_bytes(), &r.signature.sig), Code::BadSignature, "resolution signature invalid");
        let mut rec = ResolutionRec { version: q.version, rejected: r.rejected, resolution: bytes.to_vec(),
            discarded_members: BTreeSet::new(), discarded_devices: BTreeSet::new() };
        if r.chosen == known {
            self.equivocation = None;
            self.resolutions.push(rec);
            return Ok(Resolved { switched: false, head: self.head_version(), version: q.version });
        }
        let mut next = self.prefix((q.version - 1) as usize)?;
        next.append(&q.conflicting, now)?;
        ensure!(next.head().owner == resolver, Code::RosterRejected, "the chosen branch makes another member owner: the resolver cannot choose it");
        rec.discarded_members = self.seen_members.difference(&next.seen_members).cloned().collect();
        rec.discarded_devices = self.seen_devices.difference(&next.seen_devices).cloned().collect();
        next.seen_members.extend(rec.discarded_members.iter().cloned());
        next.seen_devices.extend(rec.discarded_devices.iter().cloned());
        next.resolutions.push(rec);
        *self = next;
        Ok(Resolved { switched: true, head: q.version, version: q.version })
    }

    /// Build and sign (with `sk`, the identity key of `device`) the owner's resolution choosing `chosen`
    /// ("known" or "conflicting") for the recorded equivocation.
    pub fn sign_resolution(&self, device: Id16, sk: &[u8; 32], chosen: &str) -> Res<Resolution> {
        let q = self.equivocation.as_ref().ok_or_else(|| e(Code::InvalidInput, "no equivocation recorded"))?;
        let known = self.hash_of(q.version).expect("equivocation version is in the chain");
        Resolution::sign(self.team_id, q.version, known, &q.conflicting, chosen, device, sk)
    }

    /// Validate the transition P → `u.projection` against this chain (whose head must be P).
    pub fn check_transition(&self, p: &Projection, u: &RosterUpdate, now: u64) -> Res<()> {
        let n = &u.projection;
        ensure!(n.version == p.version + 1, Code::RosterRejected, "version must be previous + 1");
        ensure!(n.prev_hash == Some(p.hash()), Code::RosterRejected, "prevHash != hash of previous version");
        ensure!(n.team_id == Some(self.team_id), Code::RosterRejected, "teamId mismatch");
        check_invariants(n)?;
        ensure!(n.recovery.time_lock >= MIN_TIME_LOCK, Code::RosterRejected, "timeLock below 24 h");

        let changes = diff(p, n)?;
        // Certificates for every added device; ids never seen before (N2b).
        for ch in &changes {
            match ch {
                Change::AddMember(m) => {
                    ensure!(!self.seen_members.contains(&hex(m)), Code::RosterRejected, "memberId {} already appeared in this chain (no reuse, N2b)", hex(m));
                }
                Change::AddDevice { member, device } => {
                    ensure!(!self.seen_devices.contains(&hex(device)), Code::RosterRejected, "deviceId {} already appeared in this chain (no reuse, N2b)", hex(device));
                    let me = n.member(member).unwrap();
                    let de = me.devices.iter().find(|d| &d.device_id == device).unwrap();
                    let c = find_cert(u, device)?;
                    check_new_device_cert(self, c, me, de, n.version)?;
                    let member_is_new = p.member(member).is_none();
                    let issuer_key = if c.self_root() {
                        ensure!(member_is_new, Code::RosterRejected, "self-issued certificate only for a new member's root device");
                        c.identity_key
                    } else if let Some((im, _)) = p.active_device(&c.issuer_device_id) {
                        ensure!(&im.member_id == member, Code::RosterRejected, "certificate issued by another member's device");
                        self.cert(&c.issuer_device_id).ok_or_else(|| e(Code::RosterRejected, "issuer cert unknown"))?.identity_key
                    } else {
                        // Sibling device of a brand-new member, itself self-issued in this update.
                        ensure!(member_is_new, Code::RosterRejected, "certificate issuer is not a non-revoked device of the member");
                        let ic = find_cert(u, &c.issuer_device_id)?;
                        ensure!(ic.self_root() && ic.member_id == *member && me.devices.iter().any(|d| d.device_id == ic.device_id),
                            Code::RosterRejected, "certificate issuer is not a root device of the new member");
                        ic.identity_key
                    };
                    ensure!(c.verify(&issuer_key), Code::RosterRejected, "DeviceCertificate signature invalid");
                }
                Change::RevokeDevice { device, rev, .. } => {
                    ensure!(rev.version == n.version && rev.compromised_since <= n.version, Code::RosterRejected, "bad revocation versions");
                    // Cascade (R2-10): devices issued by the revoked one at/after compromisedSince.
                    for m in &n.members {
                        for d in &m.devices {
                            let issued_by_revoked = self.cert(&d.device_id).map(|c| c.issuer_device_id == *device && c.device_id != *device).unwrap_or(false)
                                || u.certs.iter().any(|c| c.device_id == d.device_id && c.issuer_device_id == *device && c.device_id != *device);
                            if issued_by_revoked && d.added_in_version >= rev.compromised_since {
                                ensure!(d.revoked.is_some(), Code::RosterRejected,
                                    "cascade: device {} issued by revoked {} after compromisedSince must be revoked", hex(&d.device_id), hex(device));
                            }
                        }
                    }
                }
                _ => {}
            }
        }
        ensure!(u.certs.iter().all(|c| changes.iter().any(|ch| matches!(ch, Change::AddDevice { device, .. } if *device == c.device_id))),
            Code::RosterRejected, "certificate for a device not added in this update");

        // All listed signatures must verify against P's non-revoked devices.
        let mut signers: Vec<(Id16, Role)> = vec![];
        for s in &u.signatures {
            let (m, _) = p.active_device(&s.device_id).ok_or_else(|| e(Code::RosterRejected, format!("signer {} is not a non-revoked device in the previous version", hex(&s.device_id))))?;
            let c = self.cert(&s.device_id).ok_or_else(|| e(Code::RosterRejected, "signer cert unknown"))?;
            ensure!(util::ed25519_verify(&c.identity_key, &n.signed_bytes(), &s.sig), Code::BadSignature, "roster signature invalid");
            signers.push((m.member_id, m.role));
        }

        if changes.iter().any(|c| matches!(c, Change::Owner)) {
            return self.check_owner_change(p, u, &changes, &signers, now);
        }
        ensure!(!signers.is_empty(), Code::RosterRejected, "unsigned roster update");
        let ok = signers.iter().any(|(m, r)| changes.iter().all(|c| self.permits(c, m, *r, p, n, u)));
        ensure!(ok, Code::RosterRejected, "no signer whose role in v{} permits every change ({})", p.version,
            changes.iter().map(|c| c.name()).collect::<Vec<_>>().join(", "));
        Ok(())
    }

    fn permits(&self, c: &Change, signer: &Id16, r: Role, p: &Projection, n: &Projection, u: &RosterUpdate) -> bool {
        let manages = |target: &Id16| -> bool {
            match p.role(target).or_else(|| n.role(target)) {
                Some(Role::Member) => r.is_admin(),
                Some(Role::Admin) => r == Role::Owner,
                _ => false,
            }
        };
        match c {
            Change::Owner => false,
            Change::Quorum => r == Role::Owner,
            Change::Pins | Change::Access => r.is_admin(),
            Change::PrivateCommit | Change::Cause => true,
            Change::AddMember(m) => match n.role(m) {
                Some(Role::Member) => r.is_admin(),
                Some(Role::Admin) => r == Role::Owner,
                _ => false,
            },
            Change::RemoveMember(m) => (m == signer && p.role(m) != Some(Role::Owner)) || manages(m),
            Change::RoleChange { from, to, .. } => r == Role::Owner && *from != Role::Owner && *to != Role::Owner,
            Change::AddDevice { member, .. } | Change::RevokeDevice { member, .. } => member == signer || manages(member),
            Change::Recovery => r == Role::Owner && u.recovery_key_change_sig.is_some_and(|s| util::ed25519_verify(
                &p.recovery.owner_recovery_key, &recovery_change_bytes(&self.team_id, n.version, &n.recovery), &s)),
            Change::PendingSet => n.pending.as_ref().is_some_and(|pd| {
                let pr = &pd.proposal;
                pr.team_id == self.team_id && pr.base_version == p.version && pr.base_prev_hash == p.hash()
                    && pr.not_before >= pr.created_at.saturating_add(p.recovery.time_lock)
                    && util::ed25519_verify(&p.recovery.owner_recovery_key, &pr.signed_bytes(), &pd.sig)
            }),
            // Clearing a pending proposal without applying it is the owner's veto.
            Change::PendingClear => r == Role::Owner,
        }
    }

    fn check_owner_change(&self, p: &Projection, u: &RosterUpdate, changes: &[Change], signers: &[(Id16, Role)], now: u64) -> Res<()> {
        let n = &u.projection;
        let (old, new) = (p.owner, n.owner);
        // Only the two people involved may change; everything else must stay as is.
        let involved = |m: &Id16| *m == old || *m == new;
        for c in changes {
            let fine = match c {
                Change::Owner | Change::PrivateCommit | Change::Cause => true,
                Change::RoleChange { member, .. } => involved(member),
                Change::RemoveMember(m) => *m == old,
                Change::PendingClear => p.pending.is_some(),
                Change::AddMember(m) | Change::AddDevice { member: m, .. } => *m == new && p.pending.is_some(),
                _ => false,
            };
            ensure!(fine, Code::RosterRejected, "owner change may not also change '{}'", c.name());
        }
        if let Some(o) = &u.ownership {
            // (a) OwnershipOffer by current owner + Accept by recipient (§4.3).
            let of = &o.offer;
            ensure!(of.team_id == self.team_id && of.from == old && of.to == new, Code::RosterRejected, "offer does not match the owner change");
            ensure!(of.roster_version == n.version && Some(of.prev_hash) == n.prev_hash, Code::RosterRejected, "offer bound to another roster version");
            ensure!(of.expires_at >= now, Code::RosterRejected, "ownership offer expired");
            ensure!(p.member(&new).is_some(), Code::RosterRejected, "new owner must be a member");
            ensure!(of.new_role_of_from != Role::Owner && n.role(&old) == Some(of.new_role_of_from), Code::RosterRejected, "old owner role != offer.newRoleOfFrom");
            ensure!(p.pending.is_none() || n.pending.is_none(), Code::RosterRejected, "bad pending state");
            let check = |s: &Sig, who: &Id16, msg: &[u8]| -> Res<()> {
                let (m, _) = p.active_device(&s.device_id).ok_or_else(|| e(Code::RosterRejected, "ownership signer not an active device"))?;
                ensure!(&m.member_id == who, Code::RosterRejected, "ownership signature by the wrong member");
                let c = self.cert(&s.device_id).ok_or_else(|| e(Code::RosterRejected, "cert unknown"))?;
                ensure!(util::ed25519_verify(&c.identity_key, msg, &s.sig), Code::BadSignature, "ownership signature invalid");
                Ok(())
            };
            check(&o.offer_sig, &old, &of.offer_bytes())?;
            check(&o.accept_sig, &new, &of.accept_bytes())?;
            return Ok(());
        }
        // (b) D-3 recovery apply.
        let pd = p.pending.as_ref().ok_or_else(|| e(Code::RosterRejected, "owner change without ownership offer or pending recovery (admins cannot change the owner)"))?;
        ensure!(n.pending.is_none(), Code::RosterRejected, "apply must clear pending");
        ensure!(new == pd.proposal.new_owner, Code::RosterRejected, "new owner != recovery proposal");
        ensure!(n.active_device(&pd.proposal.new_owner_device).is_some_and(|(m, _)| m.member_id == new), Code::RosterRejected, "newOwnerDevice not an active device of the new owner");
        ensure!(now >= pd.proposal.not_before, Code::RosterRejected, "recovery proposal not applicable before notBefore");
        ensure!(!signers.is_empty(), Code::RosterRejected, "recovery apply unsigned");
        // Time evidence: signed heartbeats (not the server clock) of the admin quorum, >= notBefore.
        if p.admin_count() > 0 {
            let mut voters = BTreeSet::new();
            for hb in &u.evidence {
                let Ok((h, signed, sig)) = Heartbeat::parse(hb) else { continue };
                if h.team_id != self.team_id || h.roster_version != p.version || h.roster_hash != p.hash() || h.time < pd.proposal.not_before { continue; }
                let Some((m, _)) = p.active_device(&h.signer_device_id) else { continue };
                if m.role != Role::Admin { continue; }
                let Some(c) = self.cert(&h.signer_device_id) else { continue };
                if util::ed25519_verify(&c.identity_key, &signed, &sig) { voters.insert(m.member_id); }
            }
            ensure!(voters.len() >= p.effective_quorum(), Code::RosterRejected,
                "recovery apply lacks heartbeat evidence from {} admin(s) dated >= notBefore", p.effective_quorum());
        }
        Ok(())
    }
}

fn find_cert<'a>(u: &'a RosterUpdate, device: &Id16) -> Res<&'a DeviceCert> {
    u.certs.iter().find(|c| &c.device_id == device).ok_or_else(|| e(Code::RosterRejected, format!("missing DeviceCertificate for {}", hex(device))))
}

fn check_new_device_cert(chain: &Chain, c: &DeviceCert, m: &MemberEntry, d: &DeviceEntry, version: u64) -> Res<()> {
    ensure!(c.hash() == d.cert_hash, Code::RosterRejected, "certHash mismatch");
    ensure!(c.server_id == chain.server_id, Code::RosterRejected, "certificate for another server");
    ensure!(c.member_id == m.member_id && c.device_id == d.device_id, Code::RosterRejected, "certificate ids mismatch");
    ensure!(c.identity_key_hash() == d.identity_key_hash && c.mls_key_hash() == d.mls_key_hash, Code::RosterRejected, "certificate keys != roster key hashes");
    ensure!(d.added_in_version == version, Code::RosterRejected, "addedInVersion must be the version that adds the device");
    ensure!(d.revoked.is_none(), Code::RosterRejected, "device added already revoked");
    Ok(())
}

fn check_invariants(n: &Projection) -> Res<()> {
    ensure!(n.members.windows(2).all(|w| w[0].member_id < w[1].member_id), Code::RosterRejected, "members not strictly sorted");
    let owners: Vec<_> = n.members.iter().filter(|m| m.role == Role::Owner).collect();
    ensure!(owners.len() == 1 && owners[0].member_id == n.owner, Code::RosterRejected, "owner must be exactly one member with role owner");
    let mut devs = BTreeSet::new();
    for m in &n.members {
        for d in &m.devices {
            ensure!(devs.insert(d.device_id), Code::RosterRejected, "deviceId {} listed twice", hex(&d.device_id));
            if let Some(r) = &d.revoked { ensure!(r.version >= d.added_in_version, Code::RosterRejected, "revoked before added"); }
        }
        ensure!(m.devices.windows(2).all(|w| w[0].device_id < w[1].device_id), Code::RosterRejected, "devices not strictly sorted");
    }
    ensure!(n.access.windows(2).all(|w| (w[0].project_id, w[0].member_id) < (w[1].project_id, w[1].member_id)), Code::RosterRejected, "access not strictly sorted");
    ensure!(n.access.iter().all(|a| n.member(&a.member_id).is_some()), Code::RosterRejected, "access entry for unknown member");
    ensure!(n.heartbeat_quorum >= 1, Code::RosterRejected, "heartbeatQuorum must be >= 1");
    ensure!(n.pins.len() <= 2, Code::RosterRejected, "at most two SPKI pins");
    ensure!(n.recovery.time_lock >= MIN_TIME_LOCK, Code::RosterRejected, "timeLock below 24 h");
    if let Some(c) = &n.recovery.custodian { ensure!(n.member(c).is_some(), Code::RosterRejected, "custodian not a member"); }
    Ok(())
}

#[derive(Debug, Clone)]
enum Change {
    Owner,
    Recovery,
    Quorum,
    Pins,
    Access,
    PrivateCommit,
    Cause,
    PendingSet,
    PendingClear,
    AddMember(Id16),
    RemoveMember(Id16),
    RoleChange { member: Id16, from: Role, to: Role },
    AddDevice { member: Id16, device: Id16 },
    RevokeDevice { member: Id16, device: Id16, rev: Revocation },
}

impl Change {
    fn name(&self) -> String {
        match self {
            Change::Owner => "owner".into(), Change::Recovery => "recovery".into(), Change::Quorum => "heartbeatQuorum".into(),
            Change::Pins => "pins".into(), Change::Access => "access".into(), Change::PrivateCommit => "privateCommit".into(),
            Change::Cause => "cause".into(), Change::PendingSet => "pending(set)".into(), Change::PendingClear => "pending(clear)".into(),
            Change::AddMember(m) => format!("add member {}", hex(m)), Change::RemoveMember(m) => format!("remove member {}", hex(m)),
            Change::RoleChange { member, from, to } => format!("role {} {}→{}", hex(member), from.as_str(), to.as_str()),
            Change::AddDevice { device, .. } => format!("add device {}", hex(device)),
            Change::RevokeDevice { device, .. } => format!("revoke device {}", hex(device)),
        }
    }
}

fn diff(p: &Projection, n: &Projection) -> Res<Vec<Change>> {
    let mut out = vec![];
    if p.owner != n.owner { out.push(Change::Owner); }
    if p.recovery != n.recovery { out.push(Change::Recovery); }
    if p.heartbeat_quorum != n.heartbeat_quorum { out.push(Change::Quorum); }
    if p.pins != n.pins { out.push(Change::Pins); }
    if p.access != n.access { out.push(Change::Access); }
    if p.private_commit != n.private_commit { out.push(Change::PrivateCommit); }
    if p.cause != n.cause { out.push(Change::Cause); }
    match (&p.pending, &n.pending) {
        (None, Some(_)) => out.push(Change::PendingSet),
        (Some(_), None) => out.push(Change::PendingClear),
        (Some(a), Some(b)) => ensure!(a == b, Code::RosterRejected, "pending proposal cannot be replaced"),
        (None, None) => {}
    }
    for nm in &n.members {
        match p.member(&nm.member_id) {
            None => {
                out.push(Change::AddMember(nm.member_id));
                for d in &nm.devices { out.push(Change::AddDevice { member: nm.member_id, device: d.device_id }); }
            }
            Some(pm) => {
                if pm.role != nm.role { out.push(Change::RoleChange { member: nm.member_id, from: pm.role, to: nm.role }); }
                for pd in &pm.devices {
                    let nd = nm.devices.iter().find(|d| d.device_id == pd.device_id)
                        .ok_or_else(|| e(Code::RosterRejected, format!("device {} deleted instead of revoked", hex(&pd.device_id))))?;
                    let mut same = nd.clone();
                    same.revoked = pd.revoked.clone();
                    ensure!(&same == pd, Code::RosterRejected, "device entry {} is immutable", hex(&pd.device_id));
                    match (&pd.revoked, &nd.revoked) {
                        (None, Some(r)) => out.push(Change::RevokeDevice { member: nm.member_id, device: nd.device_id, rev: r.clone() }),
                        (Some(a), Some(b)) => ensure!(a == b, Code::RosterRejected, "revocation is immutable"),
                        (Some(_), None) => return err(Code::RosterRejected, "a revoked device cannot be reinstated"),
                        (None, None) => {}
                    }
                }
                for nd in &nm.devices {
                    if pm.devices.iter().all(|d| d.device_id != nd.device_id) {
                        out.push(Change::AddDevice { member: nm.member_id, device: nd.device_id });
                    }
                }
            }
        }
    }
    for pm in &p.members {
        if n.member(&pm.member_id).is_none() { out.push(Change::RemoveMember(pm.member_id)); }
    }
    Ok(out)
}

/// What choosing `other` instead of `branch` (both descending from `parent`) undoes (§7 Ombra,
/// owner's equivocation screen): device revocations, member removals, role demotions, an owner
/// change, and `pins`/`access` changes made in `branch` that `other` does not carry.
pub fn undone(parent: &Projection, branch: &Projection, other: &Projection) -> Res<Vec<serde_json::Value>> {
    use serde_json::json;
    let rank = |r: Role| match r { Role::Owner => 3, Role::Admin => 2, Role::Member => 1 };
    let hexes = |v: &[H32]| v.iter().map(|x| hex(x)).collect::<Vec<_>>();
    let mut out = vec![];
    for m in &branch.members {
        for d in &m.devices {
            let Some(r) = &d.revoked else { continue };
            let before = parent.device(&d.device_id).is_some_and(|(_, pd)| pd.revoked.is_some());
            let kept = other.device(&d.device_id).is_some_and(|(_, od)| od.revoked.is_some());
            if !before && !kept {
                out.push(json!({"kind": "revokeDevice", "memberId": hex(&m.member_id), "deviceId": hex(&d.device_id), "version": r.version}));
            }
        }
    }
    for pm in &parent.members {
        if branch.member(&pm.member_id).is_none() && other.member(&pm.member_id).is_some() {
            out.push(json!({"kind": "removeMember", "memberId": hex(&pm.member_id), "role": pm.role.as_str()}));
        }
    }
    for bm in &branch.members {
        if let (Some(before), Some(alt)) = (parent.role(&bm.member_id), other.role(&bm.member_id)) {
            if rank(bm.role) < rank(before) && rank(alt) > rank(bm.role) {
                out.push(json!({"kind": "demote", "memberId": hex(&bm.member_id), "from": before.as_str(), "to": bm.role.as_str(), "restored": alt.as_str()}));
            }
        }
    }
    if branch.owner != parent.owner && other.owner != branch.owner {
        out.push(json!({"kind": "owner", "from": hex(&parent.owner), "to": hex(&branch.owner), "restored": hex(&other.owner)}));
    }
    if branch.pins != parent.pins && other.pins != branch.pins {
        out.push(json!({"kind": "pins", "from": hexes(&parent.pins), "to": hexes(&branch.pins), "restored": hexes(&other.pins)}));
    }
    let access = |a: &AccessEntry, kind: &str| json!({"kind": kind, "projectId": hex(&a.project_id), "memberId": hex(&a.member_id),
        "level": match a.level { Level::Viewer => "viewer", Level::Contributor => "contributor" }});
    for a in &parent.access {
        if !branch.access.contains(a) && other.access.contains(a) { out.push(access(a, "revokeAccess")); }
    }
    for a in &branch.access {
        if !parent.access.contains(a) && !other.access.contains(a) { out.push(access(a, "grantAccess")); }
    }
    Ok(out)
}
