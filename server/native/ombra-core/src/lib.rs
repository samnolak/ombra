//! ombra-core: OpenMLS 0.9.1 + the single policy validator for Ombra and Nightly
//! (docs/SERVER_DESIGN.md §0, §3, §4, §7). One C ABI (`ffi.rs`), JSON in/out (`api.rs`).
//!
//! Module map (each module documents its invariants):
//! - `cbor`        deterministic CBOR (RFC 8949 §4.2.1), strict decoder
//! - `cert`        DeviceCertificate
//! - `roster`      signed roster chain validator (§4.1, D-3, N2b)
//! - `descriptor`  group descriptor / groupId (§3.1, R5-01)
//! - `mls`         OpenMLS groups + commit/Welcome/KeyPackage policy (§3.1)
//! - `itemop`      item-op wire format and inner signature (§3.0, R5-03)
//! - `replica`     item-op rules and local replica (§3.0)
//! - `heartbeat`, `formats` canonical fixed-layout formats (heartbeat, bind, signed Invite, envelope, hello, RemovalNotice)
//! - `pairing`     device pairing: QR payload, manual code, commit-then-reveal SAS (§2.3)
//! - `store`       state.db (SQLite), row encryption, per-team keys, receipts, security log
//! - `state`       persistent state + transactions
//! - `api`, `ffi`  JSON commands and the C ABI (the only `unsafe`)

pub mod util;
pub mod cbor;
pub mod cert;
pub mod roster;
pub mod descriptor;
pub mod heartbeat;
pub mod formats;
pub mod pairing;
pub mod adminkeys;
pub mod itemop;
pub mod replica;
pub mod store;
pub mod outbox;
pub mod state;
pub mod mls;
pub mod names;
pub mod progress;
pub mod api;
pub mod public;
pub mod ffi;
#[cfg(feature = "test-attacks")]
pub mod attack;
