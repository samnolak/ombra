//! C ABI (§7): the only `unsafe` code of the crate. Five functions:
//!
//! ```c
//! typedef struct { uint8_t *ptr; size_t len; } OmbraBuf;            // owned by Rust, free with ombra_buf_free
//! uint64_t ombra_state_open(const uint8_t *path, size_t path_len,    // UTF-8 path of the state file
//!                           const uint8_t *key, size_t key_len,      // 32-byte AES-256 key from OS key store
//!                           const uint8_t *install, size_t install_len,
//!                           OmbraBuf *err);                         // 0 on failure, *err = JSON error
//! int32_t  ombra_state_call(uint64_t h, const uint8_t *req, size_t req_len, OmbraBuf *out); // verify_and_apply
//! int32_t  ombra_state_close(uint64_t h);
//! int32_t  ombra_call(const uint8_t *req, size_t req_len, OmbraBuf *out);                   // stateless verify
//! void     ombra_buf_free(OmbraBuf b);
//! ```
//! Return codes: 0 = `*out` holds a JSON response (which itself reports ok/rejection);
//! -1 unknown handle, -2 invalid arguments (null pointer, non-UTF-8 JSON), -3 internal panic
//! (caught; never unwinds across the boundary).
//!
//! Safety contract for callers: every `(ptr, len)` pair is readable for `len` bytes for the duration
//! of the call (or `len == 0`), `out`/`err` point to writable `OmbraBuf`, and each returned buffer is
//! released exactly once with `ombra_buf_free`. Handles are opaque integers (not pointers): a stale or
//! forged handle yields -1 instead of undefined behaviour. Calls on one handle are serialised by a
//! mutex; different handles run in parallel.

use std::collections::HashMap;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use serde_json::json;

use crate::api;
use crate::state::Session;

#[repr(C)]
pub struct OmbraBuf {
    pub ptr: *mut u8,
    pub len: usize,
}

static SESSIONS: Mutex<Option<HashMap<u64, Arc<Mutex<Session>>>>> = Mutex::new(None);
static NEXT: AtomicU64 = AtomicU64::new(1);

/// SAFETY (caller contract above): `p` is valid for `n` bytes, or `n == 0`.
unsafe fn input<'a>(p: *const u8, n: usize) -> Option<&'a [u8]> {
    if n == 0 { return Some(&[]); }
    if p.is_null() { return None; }
    Some(std::slice::from_raw_parts(p, n))
}

/// SAFETY: `out` must be null or point to a writable `OmbraBuf`.
unsafe fn emit(out: *mut OmbraBuf, v: Vec<u8>) -> bool {
    if out.is_null() { return false; }
    let b = v.into_boxed_slice();
    let len = b.len();
    let ptr = Box::into_raw(b) as *mut u8;
    out.write(OmbraBuf { ptr, len });
    true
}

fn json_bytes(v: serde_json::Value) -> Vec<u8> { serde_json::to_vec(&v).unwrap_or_else(|_| b"{\"ok\":false}".to_vec()) }

fn parse(req: &[u8]) -> Option<serde_json::Value> { serde_json::from_slice(req).ok() }

fn session(h: u64) -> Option<Arc<Mutex<Session>>> {
    SESSIONS.lock().ok()?.as_ref()?.get(&h).cloned()
}

#[no_mangle]
pub unsafe extern "C" fn ombra_state_open(path: *const u8, path_len: usize, key: *const u8, key_len: usize,
                                          install: *const u8, install_len: usize, err: *mut OmbraBuf) -> u64 {
    let r = catch_unwind(AssertUnwindSafe(|| -> Result<u64, serde_json::Value> {
        let bad = |m: &str| json!({"ok": false, "code": "invalid-input", "error": m});
        let path = input(path, path_len).and_then(|p| std::str::from_utf8(p).ok()).ok_or_else(|| bad("path"))?;
        let key: [u8; 32] = input(key, key_len).and_then(|k| k.try_into().ok()).ok_or_else(|| bad("key must be 32 bytes"))?;
        let install = input(install, install_len).ok_or_else(|| bad("installId"))?;
        let s = Session::open(&PathBuf::from(path), key, install).map_err(|x| api::fail(&x))?;
        let h = NEXT.fetch_add(1, Ordering::Relaxed);
        SESSIONS.lock().map_err(|_| bad("lock"))?.get_or_insert_with(HashMap::new).insert(h, Arc::new(Mutex::new(s)));
        Ok(h)
    }));
    match r {
        Ok(Ok(h)) => h,
        Ok(Err(j)) => { emit(err, json_bytes(j)); 0 }
        Err(_) => { emit(err, json_bytes(json!({"ok": false, "code": "internal", "error": "panic"}))); 0 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn ombra_state_call(h: u64, req: *const u8, req_len: usize, out: *mut OmbraBuf) -> i32 {
    let Some(req) = input(req, req_len).and_then(parse) else { return -2 };
    if out.is_null() { return -2; }
    let Some(s) = session(h) else { return -1 };
    match catch_unwind(AssertUnwindSafe(|| {
        let mut g = s.lock().unwrap_or_else(|p| p.into_inner());
        api::call_stateful(&mut g, req)
    })) {
        Ok(v) => { emit(out, json_bytes(v)); 0 }
        Err(_) => -3,
    }
}

#[no_mangle]
pub extern "C" fn ombra_state_close(h: u64) -> i32 {
    let removed = SESSIONS.lock().ok().and_then(|mut g| g.as_mut().and_then(|m| m.remove(&h)));
    if removed.is_some() { 0 } else { -1 }
}

#[no_mangle]
pub unsafe extern "C" fn ombra_call(req: *const u8, req_len: usize, out: *mut OmbraBuf) -> i32 {
    let Some(req) = input(req, req_len).and_then(parse) else { return -2 };
    if out.is_null() { return -2; }
    match catch_unwind(AssertUnwindSafe(|| api::call_stateless(req))) {
        Ok(v) => { emit(out, json_bytes(v)); 0 }
        Err(_) => -3,
    }
}

#[no_mangle]
pub unsafe extern "C" fn ombra_buf_free(b: OmbraBuf) {
    if !b.ptr.is_null() {
        // SAFETY: (ptr, len) came from `emit` (Box<[u8]>::into_raw) and is freed exactly once.
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(b.ptr, b.len)));
    }
}
