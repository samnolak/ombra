//! DeviceCertificate (§2.1, §2.3): binds `deviceId` to its Ed25519 identityKey and MLS signature
//! key, issued (signed) by an identityKey of a device of the same member. A member's first
//! device (root, new member or genesis) is self-issued: `issuerDeviceId == deviceId`.
//!
//! Wire: canonical CBOR map `{serverId, memberId, deviceId, identityKey, mlsSignatureKey,
//! issuerDeviceId, pairingNonce, issuedAt, sig}`;
//! `sig = Ed25519(issuer identityKey, "ombra/v1/device-cert" ‖ 0x00 ‖ canonical(map without sig))`;
//! `certHash = SHA-256(canonical(map with sig))` is what the roster pins.

use serde::{Deserialize, Serialize};

use crate::cbor::{self, b, Fields, MapB, Value};
use crate::util::{self, hexser, Code, Id16, Res, H32};

pub const CERT_DOMAIN: &[u8] = b"ombra/v1/device-cert";

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceCert {
    #[serde(with = "hexser")] pub server_id: Id16,
    #[serde(with = "hexser")] pub member_id: Id16,
    #[serde(with = "hexser")] pub device_id: Id16,
    #[serde(with = "hexser")] pub identity_key: [u8; 32],
    #[serde(with = "hexser")] pub mls_signature_key: [u8; 32],
    #[serde(with = "hexser")] pub issuer_device_id: Id16,
    #[serde(with = "hexser")] pub pairing_nonce: Vec<u8>,
    pub issued_at: u64,
    #[serde(with = "hexser")] pub sig: [u8; 64],
}

impl DeviceCert {
    fn tbs(&self) -> MapB {
        MapB::new()
            .put("serverId", b(&self.server_id))
            .put("memberId", b(&self.member_id))
            .put("deviceId", b(&self.device_id))
            .put("identityKey", b(&self.identity_key))
            .put("mlsSignatureKey", b(&self.mls_signature_key))
            .put("issuerDeviceId", b(&self.issuer_device_id))
            .put("pairingNonce", b(&self.pairing_nonce))
            .put("issuedAt", Value::U(self.issued_at))
    }

    pub fn signed_bytes(&self) -> Vec<u8> {
        let mut m = CERT_DOMAIN.to_vec();
        m.push(0);
        m.extend(cbor::encode(&self.tbs().build()));
        m
    }

    pub fn to_bytes(&self) -> Vec<u8> { cbor::encode(&self.tbs().put("sig", b(&self.sig)).build()) }

    pub fn hash(&self) -> H32 { util::sha256(&[&self.to_bytes()]) }

    pub fn self_root(&self) -> bool { self.issuer_device_id == self.device_id }

    pub fn identity_key_hash(&self) -> H32 { util::sha256(&[&self.identity_key]) }
    pub fn mls_key_hash(&self) -> H32 { util::sha256(&[&self.mls_signature_key]) }

    pub fn verify(&self, issuer_identity_key: &[u8]) -> bool {
        util::ed25519_verify(issuer_identity_key, &self.signed_bytes(), &self.sig)
    }

    pub fn from_value(v: Value) -> Res<Self> {
        let mut f = Fields::new(v, "DeviceCertificate")?;
        let c = DeviceCert {
            server_id: f.req("serverId")?.fixed()?,
            member_id: f.req("memberId")?.fixed()?,
            device_id: f.req("deviceId")?.fixed()?,
            identity_key: f.req("identityKey")?.fixed()?,
            mls_signature_key: f.req("mlsSignatureKey")?.fixed()?,
            issuer_device_id: f.req("issuerDeviceId")?.fixed()?,
            pairing_nonce: f.req("pairingNonce")?.bytes()?.to_vec(),
            issued_at: f.req("issuedAt")?.u()?,
            sig: f.req("sig")?.fixed()?,
        };
        f.finish()?;
        crate::ensure!(c.pairing_nonce.len() <= 64, Code::InvalidInput, "pairingNonce too long");
        Ok(c)
    }

    pub fn parse(bytes: &[u8]) -> Res<Self> { Self::from_value(cbor::decode(bytes)?) }

    /// Sign with the issuer's identity secret key (`issuer_device_id` must be set by the caller).
    pub fn sign(mut self, issuer_sk: &[u8; 32]) -> Self {
        self.sig = util::ed25519_sign(issuer_sk, &self.signed_bytes());
        self
    }
}
