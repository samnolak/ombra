//! Fixed-layout canonical formats verified statelessly: invite secrets, `bind` and the signed
//! Invite (§3.4), Envelope (§6.1), hello (§2.2), RemovalNotice (§5.1 p.6).

use crate::cbor::{self, b, t, Fields, MapB, Value};
use crate::ensure;
use crate::heartbeat::Reader;
use crate::roster::domain;
use crate::util::{self, Code, Id16, Res, H32};

pub const INVITE_SERVER_INFO: &[u8] = b"ombra/v1/invite/server";
pub const INVITE_ADMIN_INFO: &[u8] = b"ombra/v1/invite/admin";
pub const BIND_DOMAIN: &[u8] = b"ombra/v1/invite-bind";
pub const ENVELOPE_DOMAIN: &[u8] = b"ombra/v1/envelope";
pub const HELLO_DOMAIN: &[u8] = b"ombra/v1/hello";
pub const REMOVAL_DOMAIN: &[u8] = b"ombra/v1/removal-notice";
pub const INVITE_DOMAIN: &[u8] = b"ombra/v1/invite";

/// Signed Invite (§3.4 п.1): `{inviteId, teamId, rosterHead: {version, hash}, role, expiresAt,
/// maxUses, verifier, inviterDeviceId, inviterIdentityKey}` in canonical CBOR plus `sig =
/// Ed25519(inviter identityKey, "ombra/v1/invite" ‖ 0x00 ‖ canonical(map without sig))`. The invitee
/// checks `SHA-256(inviterIdentityKey) = a` from the link, the signature, and that the inviter is an
/// active owner/admin device of `rosterHead` whose certificate carries that key (`team.import`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Invite {
    pub invite_id: Id16,
    pub team_id: H32,
    pub head_version: u64,
    pub head_hash: H32,
    pub role: String,
    pub expires_at: u64,
    pub max_uses: u64,
    pub verifier: H32,
    pub inviter_device_id: Id16,
    pub inviter_identity_key: H32,
    pub sig: [u8; 64],
}

impl Invite {
    fn tbs(&self) -> MapB {
        MapB::new().put("inviteId", b(&self.invite_id)).put("teamId", b(&self.team_id))
            .put("rosterHead", MapB::new().put("version", Value::U(self.head_version)).put("hash", b(&self.head_hash)).build())
            .put("role", t(&self.role)).put("expiresAt", Value::U(self.expires_at)).put("maxUses", Value::U(self.max_uses))
            .put("verifier", b(&self.verifier)).put("inviterDeviceId", b(&self.inviter_device_id))
            .put("inviterIdentityKey", b(&self.inviter_identity_key))
    }
    pub fn signed_bytes(&self) -> Vec<u8> { domain(INVITE_DOMAIN, &cbor::encode(&self.tbs().build())) }
    pub fn to_bytes(&self) -> Vec<u8> { cbor::encode(&self.tbs().put("sig", b(&self.sig)).build()) }
    pub fn sign(mut self, sk: &[u8; 32]) -> Self {
        self.sig = util::ed25519_sign(sk, &self.signed_bytes());
        self
    }
    pub fn verify(&self) -> bool { util::ed25519_verify(&self.inviter_identity_key, &self.signed_bytes(), &self.sig) }
    pub fn fingerprint(&self) -> H32 { util::sha256(&[&self.inviter_identity_key]) }

    pub fn parse(bytes: &[u8]) -> Res<Self> {
        let mut f = Fields::new(cbor::decode(bytes)?, "Invite")?;
        let mut h = Fields::new(f.req("rosterHead")?, "rosterHead")?;
        let (head_version, head_hash) = (h.req("version")?.u()?, h.req("hash")?.fixed()?);
        h.finish()?;
        let inv = Invite {
            invite_id: f.req("inviteId")?.fixed()?, team_id: f.req("teamId")?.fixed()?, head_version, head_hash,
            role: f.req("role")?.text()?.to_string(), expires_at: f.req("expiresAt")?.u()?, max_uses: f.req("maxUses")?.u()?,
            verifier: f.req("verifier")?.fixed()?, inviter_device_id: f.req("inviterDeviceId")?.fixed()?,
            inviter_identity_key: f.req("inviterIdentityKey")?.fixed()?, sig: f.req("sig")?.fixed()?,
        };
        f.finish()?;
        ensure!(inv.role == "member" || inv.role == "admin", Code::InvalidInput, "invite role must be member|admin");
        ensure!(inv.max_uses >= 1, Code::InvalidInput, "invite maxUses must be >= 1");
        Ok(inv)
    }
}

pub struct InviteKeys { pub k_srv: H32, pub k_adm: H32, pub verifier: H32 }

/// `k_srv = HKDF-SHA256(k, salt=inviteId, info="ombra/v1/invite/server")`, `k_adm` likewise with
/// ".../admin"; the server only ever sees `verifier = SHA-256(k_srv)`.
pub fn invite_keys(k: &[u8], invite_id: &Id16) -> Res<InviteKeys> {
    ensure!(k.len() == 32, Code::InvalidInput, "invite secret k must be 256 bits");
    let k_srv = util::hkdf32(k, invite_id, INVITE_SERVER_INFO);
    let k_adm = util::hkdf32(k, invite_id, INVITE_ADMIN_INFO);
    Ok(InviteKeys { k_srv, k_adm, verifier: util::sha256(&[&k_srv]) })
}

pub struct BindDevice { pub device_id: Id16, pub identity_key: [u8; 32], pub mls_signature_key: [u8; 32], pub key_package: Vec<u8> }

/// `"ombra/v1/invite-bind" ‖ 0x00 ‖ inviteId[16] ‖ memberId[16] ‖ u8 n ‖ n × (deviceId[16] ‖
/// identityKey[32] ‖ mlsSignatureKey[32] ‖ SHA-256(KeyPackage TLS bytes)[32])`, devices ascending.
pub fn bind_message(invite_id: &Id16, member_id: &Id16, devices: &[BindDevice]) -> Res<Vec<u8>> {
    ensure!(!devices.is_empty() && devices.len() <= 255, Code::InvalidInput, "bind needs 1..255 devices");
    let mut ds: Vec<&BindDevice> = devices.iter().collect();
    ds.sort_by(|a, b| a.device_id.cmp(&b.device_id));
    ensure!(ds.windows(2).all(|w| w[0].device_id != w[1].device_id), Code::InvalidInput, "duplicate deviceId in bind");
    let mut m = BIND_DOMAIN.to_vec();
    m.push(0);
    m.extend_from_slice(invite_id);
    m.extend_from_slice(member_id);
    m.push(ds.len() as u8);
    for d in ds {
        m.extend_from_slice(&d.device_id);
        m.extend_from_slice(&d.identity_key);
        m.extend_from_slice(&d.mls_signature_key);
        m.extend_from_slice(&util::sha256(&[&d.key_package]));
    }
    Ok(m)
}

pub fn bind(k_adm: &[u8], invite_id: &Id16, member_id: &Id16, devices: &[BindDevice]) -> Res<H32> {
    Ok(util::hmac256(k_adm, &bind_message(invite_id, member_id, devices)?))
}

pub fn verify_bind(k_adm: &[u8], invite_id: &Id16, member_id: &Id16, devices: &[BindDevice], tag: &[u8]) -> Res<bool> {
    Ok(util::ct_eq(&bind(k_adm, invite_id, member_id, devices)?, tag))
}

/// Envelope: body must be canonical CBOR; signature over `"ombra/v1/envelope" ‖ 0x00 ‖ body`.
pub fn envelope_message(body: &[u8]) -> Res<Vec<u8>> {
    cbor::decode(body)?;
    Ok(domain(ENVELOPE_DOMAIN, body))
}

pub fn verify_envelope(body: &[u8], sig: &[u8], identity_key: &[u8]) -> bool {
    envelope_message(body).map(|m| util::ed25519_verify(identity_key, &m, sig)).unwrap_or(false)
}

/// `"ombra/v1/hello" ‖ 0x00 ‖ serverId[16] ‖ nonce[32] ‖ deviceId[16] ‖ time u64 BE`.
pub fn hello_message(server_id: &Id16, nonce: &[u8; 32], device_id: &Id16, time: u64) -> Vec<u8> {
    let mut body = Vec::with_capacity(72);
    body.extend_from_slice(server_id);
    body.extend_from_slice(nonce);
    body.extend_from_slice(device_id);
    body.extend_from_slice(&time.to_be_bytes());
    domain(HELLO_DOMAIN, &body)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RemovalNotice { pub team_id: H32, pub member_id: Id16, pub roster_version: u64, pub time: u64, pub signer_device_id: Id16 }

impl RemovalNotice {
    /// `"ombra/v1/removal-notice" ‖ 0x00 ‖ teamId[32] ‖ memberId[16] ‖ rosterVersion u64 BE ‖
    /// time u64 BE ‖ signerDeviceId[16]`; wire = that ‖ Ed25519 signature[64].
    pub fn signed_bytes(&self) -> Vec<u8> {
        let mut body = self.team_id.to_vec();
        body.extend_from_slice(&self.member_id);
        body.extend_from_slice(&self.roster_version.to_be_bytes());
        body.extend_from_slice(&self.time.to_be_bytes());
        body.extend_from_slice(&self.signer_device_id);
        domain(REMOVAL_DOMAIN, &body)
    }

    pub fn parse(wire: &[u8]) -> Res<(RemovalNotice, Vec<u8>, [u8; 64])> {
        ensure!(wire.len() == REMOVAL_DOMAIN.len() + 1 + 32 + 16 + 8 + 8 + 16 + 64, Code::InvalidInput, "bad RemovalNotice length");
        let (body, sig) = wire.split_at(wire.len() - 64);
        let mut r = Reader { b: body, p: 0 };
        ensure!(r.take(REMOVAL_DOMAIN.len() + 1)? == domain(REMOVAL_DOMAIN, &[]).as_slice(), Code::InvalidInput, "bad RemovalNotice domain");
        let n = RemovalNotice {
            team_id: r.take(32)?.try_into().unwrap(), member_id: r.take(16)?.try_into().unwrap(),
            roster_version: r.u64()?, time: r.u64()?, signer_device_id: r.take(16)?.try_into().unwrap(),
        };
        Ok((n, body.to_vec(), sig.try_into().unwrap()))
    }
}
