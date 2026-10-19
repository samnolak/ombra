//! Device pairing (§2.3, SD-09, R2-09): the QR payload of a new device, the manual code with
//! ≥128 bits of its key fingerprint, and the commit-then-reveal SAS for the path through the server.
//!
//! SAS protocol (the server only relays): the existing device E publishes
//! `commitment = SHA-256("ombra/v1/pair-commit" ‖ 0x00 ‖ deviceId_E ‖ identityKey_E ‖ mlsKey_E ‖ nonce_E)`;
//! the new device N answers with its keys and `nonce_N` in clear; only then E reveals `nonce_E`.
//! N checks the commitment, and both show six words of
//! `SHA-256("ombra/v1/pair-sas" ‖ 0x00 ‖ E fields ‖ N fields)` (fields: deviceId ‖ identityKey ‖
//! mlsKey ‖ nonce). A relay that substitutes either key must fix its own nonce towards N before it
//! learns `nonce_N` and its nonce towards E before it learns `nonce_E`, so the two word lists
//! differ except with probability 2^-48.

use crate::cbor::{self, b, Fields, MapB, Value};
use crate::ensure;
use crate::roster::domain;
use crate::util::{self, Code, Id16, Res, H32};

pub const COMMIT_DOMAIN: &[u8] = b"ombra/v1/pair-commit";
pub const SAS_DOMAIN: &[u8] = b"ombra/v1/pair-sas";
pub const CODE_DOMAIN: &[u8] = b"ombra/v1/pair-code";
pub const SAS_WORDS: usize = 6;
/// Manual code: 160 bits of the fingerprint (≥128 required by §2.3) in 32 base32 characters.
pub const CODE_BYTES: usize = 20;
const CODE_ALPHABET: &[u8; 32] = b"ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Party { pub device_id: Id16, pub identity_key: H32, pub mls_key: H32, pub nonce: H32 }

impl Party {
    fn bytes(&self) -> Vec<u8> {
        let mut v = Vec::with_capacity(16 + 32 * 3);
        v.extend_from_slice(&self.device_id);
        v.extend_from_slice(&self.identity_key);
        v.extend_from_slice(&self.mls_key);
        v.extend_from_slice(&self.nonce);
        v
    }
}

pub fn commitment(p: &Party) -> H32 { util::sha256(&[&domain(COMMIT_DOMAIN, &p.bytes())]) }

/// Six words; `None` when the revealed existing-device data does not match its commitment.
pub fn sas(existing: &Party, existing_commitment: &[u8], new: &Party) -> Option<Vec<&'static str>> {
    if !util::ct_eq(&commitment(existing), existing_commitment) { return None; }
    let mut body = existing.bytes();
    body.extend(new.bytes());
    let h = util::sha256(&[&domain(SAS_DOMAIN, &body)]);
    Some(h[..SAS_WORDS].iter().map(|x| WORDS[*x as usize]).collect())
}

/// Manual-entry code of the new device: 32 base32 characters in groups of 4.
pub fn manual_code(p: &Party) -> String {
    let h = util::sha256(&[&domain(CODE_DOMAIN, &p.bytes())]);
    let mut bits: u64 = 0;
    let mut n = 0;
    let mut out = String::new();
    for &x in &h[..CODE_BYTES] {
        bits = (bits << 8) | x as u64;
        n += 8;
        while n >= 5 {
            n -= 5;
            if out.len() % 5 == 4 { out.push('-'); }
            out.push(CODE_ALPHABET[((bits >> n) & 31) as usize] as char);
        }
    }
    out
}

/// Normalised comparison of a typed code (case, separators and spaces ignored).
pub fn code_matches(p: &Party, typed: &str) -> bool {
    let norm = |s: &str| s.chars().filter(|c| c.is_ascii_alphanumeric()).map(|c| c.to_ascii_uppercase()).collect::<String>();
    util::ct_eq(norm(&manual_code(p)).as_bytes(), norm(typed).as_bytes())
}

/// QR payload: canonical CBOR `{v: 1, deviceId, identityKey, mlsSignatureKey, pairingNonce}` —
/// the full keys, not a reference to the server (§2.3 п.1).
pub fn qr_payload(p: &Party) -> Vec<u8> {
    cbor::encode(&MapB::new().put("v", Value::U(1)).put("deviceId", b(&p.device_id)).put("identityKey", b(&p.identity_key))
        .put("mlsSignatureKey", b(&p.mls_key)).put("pairingNonce", b(&p.nonce)).build())
}

pub fn parse_qr(bytes: &[u8]) -> Res<Party> {
    let mut f = Fields::new(cbor::decode(bytes)?, "pairing QR")?;
    ensure!(f.req("v")?.u()? == 1, Code::InvalidInput, "unsupported pairing QR version");
    let p = Party { device_id: f.req("deviceId")?.fixed()?, identity_key: f.req("identityKey")?.fixed()?,
        mls_key: f.req("mlsSignatureKey")?.fixed()?, nonce: f.req("pairingNonce")?.fixed()? };
    f.finish()?;
    Ok(p)
}

/// 256 short, mutually distinct English words: one byte of the SAS hash each.
pub const WORDS: [&str; 256] = [
    "acid", "acorn", "actor", "adobe", "agent", "alarm", "album", "alley", "amber", "ample", "angel", "ankle", "apple", "apron", "arena", "armor",
    "arrow", "aspen", "atlas", "attic", "avid", "award", "axis", "bacon", "badge", "bagel", "baker", "bamboo", "banjo", "barn", "basil", "beach",
    "beard", "bench", "berry", "bison", "blade", "blank", "blaze", "bloom", "board", "bonus", "booth", "brave", "bread", "brick", "bride", "brook",
    "brush", "bucket", "buggy", "bunny", "cabin", "cable", "cactus", "camel", "candy", "canoe", "canyon", "cargo", "carpet", "cedar", "chalk", "chess",
    "chief", "cider", "cliff", "clock", "cloud", "cobra", "cocoa", "comet", "coral", "cotton", "couch", "crane", "crater", "crown", "cube", "curry",
    "daisy", "dance", "delta", "denim", "desert", "diary", "dingo", "disco", "dolphin", "donut", "dove", "dragon", "drum", "eagle", "easel", "echo",
    "eclipse", "elbow", "elder", "ember", "emerald", "engine", "epic", "equal", "falcon", "fern", "ferry", "fiddle", "field", "flame", "flute", "focus",
    "forest", "fossil", "fox", "frost", "fudge", "galaxy", "garden", "garlic", "gecko", "giant", "ginger", "glacier", "globe", "goat", "gold", "grape",
    "gravel", "guitar", "hammer", "harbor", "hazel", "helmet", "hero", "hippo", "honey", "hornet", "hotel", "humble", "igloo", "indigo", "iris", "island",
    "ivory", "jacket", "jaguar", "jelly", "jewel", "jigsaw", "judge", "juice", "jungle", "kayak", "kernel", "kettle", "kiosk", "kitten", "koala", "ladder",
    "lagoon", "lemon", "lily", "lion", "lizard", "lobster", "locket", "lotus", "lumber", "magnet", "mango", "maple", "marble", "meadow", "melon", "mint",
    "mirror", "mitten", "mocha", "monkey", "moose", "mosaic", "motor", "muffin", "nectar", "needle", "nickel", "noodle", "nugget", "oasis", "ocean", "olive",
    "onion", "opal", "orbit", "orchid", "otter", "oyster", "paddle", "panda", "papaya", "parrot", "peach", "pebble", "pepper", "piano", "pickle", "pigeon",
    "pillow", "pirate", "planet", "plum", "polka", "pony", "poppy", "prism", "pumpkin", "puzzle", "quail", "quartz", "quiet", "quilt", "rabbit", "radar",
    "radish", "raven", "reef", "rhino", "ribbon", "river", "robin", "rocket", "rumba", "saddle", "salmon", "sandal", "satin", "scarf", "shark", "shell",
    "silver", "skate", "sloth", "snow", "sofa", "spider", "sponge", "squid", "stamp", "storm", "sugar", "summit", "sunset", "swan", "tango", "teapot",
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn words_are_distinct() {
        let set: std::collections::BTreeSet<_> = WORDS.iter().collect();
        assert_eq!(set.len(), 256);
    }
}
