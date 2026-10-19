//! Errors, hex, primitive crypto wrappers. All crypto is RustCrypto (pure Rust, no system libs).

use std::fmt;

use aes_gcm::aead::{Aead, KeyInit, Payload};
use aes_gcm::{Aes256Gcm, Nonce};
use ed25519_dalek::{Signature, Signer, SigningKey, Verifier, VerifyingKey};
use hmac::{Hmac, Mac};
use sha2::{Digest, Sha256};

/// Stable machine-readable error codes (part of the C ABI contract, see `api.rs`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Code {
    InvalidInput,
    Cbor,
    BadSignature,
    /// Roster update violates §4.1 rules.
    RosterRejected,
    /// A validly signed alternative for a known roster version without the authority to count as
    /// equivocation, or one an owner resolution rejected: refused, nothing frozen.
    RosterConflict,
    /// Incoming commit violates §3.1 staged-commit policy (probe rc=-10). State unchanged.
    CommitRejected,
    /// Local Add refused: KeyPackage/device not admissible (probe rc=-12).
    AddRejected,
    /// Welcome refused (probe rc=-12). State unchanged.
    WelcomeRejected,
    /// Item operation refused (§3.0). Not applied, not stored.
    OpRejected,
    /// Message references a roster version the caller has not verified yet; fetch and retry.
    NeedRoster,
    /// Two different valid roster versions with one number: team frozen.
    Equivocation,
    ClosedGeneration,
    Frozen,
    NotFound,
    /// MLS-layer failure (decryption, wrong epoch, eviction, malformed message).
    Mls,
    /// Persisted state belongs to another installation (profile copied, §2.1 SD-10).
    CloneDetected,
    /// Team rows are sealed with a per-team key the caller has not supplied yet (`team.unlock`).
    TeamLocked,
    Storage,
    Internal,
}

impl Code {
    pub fn as_str(self) -> &'static str {
        match self {
            Code::InvalidInput => "invalid-input",
            Code::Cbor => "cbor",
            Code::BadSignature => "bad-signature",
            Code::RosterRejected => "roster-rejected",
            Code::RosterConflict => "roster-conflict",
            Code::CommitRejected => "commit-rejected",
            Code::AddRejected => "add-rejected",
            Code::WelcomeRejected => "welcome-rejected",
            Code::OpRejected => "op-rejected",
            Code::NeedRoster => "need-roster",
            Code::Equivocation => "equivocation",
            Code::ClosedGeneration => "closed-generation",
            Code::Frozen => "frozen",
            Code::NotFound => "not-found",
            Code::Mls => "mls",
            Code::CloneDetected => "clone-detected",
            Code::TeamLocked => "team-locked",
            Code::Storage => "storage",
            Code::Internal => "internal",
        }
    }
}

#[derive(Debug, Clone)]
pub struct Error {
    pub code: Code,
    pub msg: String,
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result { write!(f, "{}: {}", self.code.as_str(), self.msg) }
}

pub type Res<T> = Result<T, Error>;

pub fn err<T>(code: Code, msg: impl Into<String>) -> Res<T> { Err(Error { code, msg: msg.into() }) }
pub fn e(code: Code, msg: impl Into<String>) -> Error { Error { code, msg: msg.into() } }

impl From<crate::cbor::CborError> for Error {
    fn from(x: crate::cbor::CborError) -> Self { e(Code::Cbor, x.0) }
}

/// Run `$check`; on `false` return `Err($code, msg)`.
#[macro_export]
macro_rules! ensure {
    ($cond:expr, $code:expr, $($fmt:tt)+) => {
        if !$cond { return Err($crate::util::e($code, format!($($fmt)+))); }
    };
}

pub fn hex(b: &[u8]) -> String {
    const H: &[u8; 16] = b"0123456789abcdef";
    let mut s = String::with_capacity(b.len() * 2);
    for x in b { s.push(H[(x >> 4) as usize] as char); s.push(H[(x & 15) as usize] as char); }
    s
}

pub fn unhex(s: &str) -> Res<Vec<u8>> {
    let s = s.as_bytes();
    ensure!(s.len() % 2 == 0, Code::InvalidInput, "odd hex length");
    let nib = |c: u8| -> Res<u8> {
        match c {
            b'0'..=b'9' => Ok(c - b'0'),
            b'a'..=b'f' => Ok(c - b'a' + 10),
            b'A'..=b'F' => Ok(c - b'A' + 10),
            _ => err(Code::InvalidInput, "bad hex digit"),
        }
    };
    s.chunks(2).map(|p| Ok((nib(p[0])? << 4) | nib(p[1])?)).collect()
}

pub fn sha256(parts: &[&[u8]]) -> [u8; 32] {
    let mut h = Sha256::new();
    for p in parts { h.update(p); }
    h.finalize().into()
}

pub fn random<const N: usize>() -> [u8; N] {
    let mut b = [0u8; N];
    getrandom::getrandom(&mut b).expect("OS RNG unavailable");
    b
}

pub fn ed25519_sign(sk: &[u8; 32], msg: &[u8]) -> [u8; 64] {
    SigningKey::from_bytes(sk).sign(msg).to_bytes()
}

pub fn ed25519_public(sk: &[u8; 32]) -> [u8; 32] {
    SigningKey::from_bytes(sk).verifying_key().to_bytes()
}

/// Strict verification (rejects non-canonical / small-order malleable signatures).
pub fn ed25519_verify(pk: &[u8], msg: &[u8], sig: &[u8]) -> bool {
    let (Ok(pk), Ok(sig)) = (<[u8; 32]>::try_from(pk), <[u8; 64]>::try_from(sig)) else { return false };
    let Ok(vk) = VerifyingKey::from_bytes(&pk) else { return false };
    let sig = Signature::from_bytes(&sig);
    vk.verify_strict(msg, &sig).is_ok() && vk.verify(msg, &sig).is_ok()
}

pub fn hkdf32(ikm: &[u8], salt: &[u8], info: &[u8]) -> [u8; 32] {
    let hk = hkdf::Hkdf::<Sha256>::new(Some(salt), ikm);
    let mut out = [0u8; 32];
    hk.expand(info, &mut out).expect("32 bytes is a valid HKDF-SHA256 length");
    out
}

pub fn hmac256(key: &[u8], msg: &[u8]) -> [u8; 32] {
    let mut m = <Hmac<Sha256> as Mac>::new_from_slice(key).expect("HMAC accepts any key length");
    m.update(msg);
    m.finalize().into_bytes().into()
}

/// Constant-time equality for MACs/verifiers.
pub fn ct_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() { return false; }
    a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

pub fn aes_gcm_seal(key: &[u8; 32], nonce: &[u8; 12], aad: &[u8], pt: &[u8]) -> Vec<u8> {
    Aes256Gcm::new(key.into()).encrypt(Nonce::from_slice(nonce), Payload { msg: pt, aad }).expect("AES-GCM encrypt")
}

pub fn aes_gcm_open(key: &[u8; 32], nonce: &[u8; 12], aad: &[u8], ct: &[u8]) -> Option<Vec<u8>> {
    Aes256Gcm::new(key.into()).decrypt(Nonce::from_slice(nonce), Payload { msg: ct, aad }).ok()
}

pub type Id16 = [u8; 16];
pub type H32 = [u8; 32];

pub fn id16(v: &[u8]) -> Res<Id16> { v.try_into().map_err(|_| e(Code::InvalidInput, "expected 16-byte id")) }
pub fn h32(v: &[u8]) -> Res<H32> { v.try_into().map_err(|_| e(Code::InvalidInput, "expected 32 bytes")) }

/// serde helpers: bytes as lowercase hex strings in the persisted JSON state.
pub mod hexser {
    use serde::{Deserialize, Deserializer, Serializer};
    pub fn serialize<S: Serializer, T: AsRef<[u8]>>(v: &T, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&super::hex(v.as_ref()))
    }
    pub fn deserialize<'de, D: Deserializer<'de>, T: TryFrom<Vec<u8>>>(d: D) -> Result<T, D::Error> {
        let s = String::deserialize(d)?;
        let v = super::unhex(&s).map_err(serde::de::Error::custom)?;
        T::try_from(v).map_err(|_| serde::de::Error::custom("wrong byte length"))
    }
}

/// serde: `Option<bytes>` as hex or null.
pub mod hexopt {
    use serde::{Deserialize, Deserializer, Serializer};
    pub fn serialize<S: Serializer, T: AsRef<[u8]>>(v: &Option<T>, s: S) -> Result<S::Ok, S::Error> {
        match v { Some(v) => s.serialize_some(&super::hex(v.as_ref())), None => s.serialize_none() }
    }
    pub fn deserialize<'de, D: Deserializer<'de>, T: TryFrom<Vec<u8>>>(d: D) -> Result<Option<T>, D::Error> {
        let Some(s) = Option::<String>::deserialize(d)? else { return Ok(None) };
        let v = super::unhex(&s).map_err(serde::de::Error::custom)?;
        T::try_from(v).map(Some).map_err(|_| serde::de::Error::custom("wrong byte length"))
    }
}

/// serde: `Vec<bytes>` as a list of hex strings.
pub mod hexvec {
    use serde::{Deserialize, Deserializer, Serializer, ser::SerializeSeq};
    pub fn serialize<S: Serializer, T: AsRef<[u8]>>(v: &[T], s: S) -> Result<S::Ok, S::Error> {
        let mut q = s.serialize_seq(Some(v.len()))?;
        for x in v { q.serialize_element(&super::hex(x.as_ref()))?; }
        q.end()
    }
    pub fn deserialize<'de, D: Deserializer<'de>, T: TryFrom<Vec<u8>>>(d: D) -> Result<Vec<T>, D::Error> {
        Vec::<String>::deserialize(d)?.into_iter().map(|s| {
            let v = super::unhex(&s).map_err(serde::de::Error::custom)?;
            T::try_from(v).map_err(|_| serde::de::Error::custom("wrong byte length"))
        }).collect()
    }
}
