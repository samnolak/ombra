//! Deterministic CBOR (RFC 8949 §4.2.1 "core deterministic encoding") for every signed or hashed
//! structure (roster projection, DeviceCertificate, group descriptor, item-op payload, envelope).
//!
//! Invariants:
//! - encoding: definite lengths only, shortest-form heads, map entries sorted by the bytewise
//!   order of their *encoded* keys, no duplicate keys, no floats/tags/undefined;
//! - decoding is strict: [`decode`] accepts only bytes that re-encode to exactly themselves, so a
//!   hash/signature over the received bytes equals one over the canonical form (no malleability).

use std::fmt;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Value {
    U(u64),
    /// Negative integer `-1 - n`.
    N(u64),
    B(Vec<u8>),
    T(String),
    A(Vec<Value>),
    /// Entries in canonical order once encoded; constructors may push in any order.
    M(Vec<(Value, Value)>),
    Bool(bool),
    Null,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CborError(pub String);

impl fmt::Display for CborError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result { write!(f, "cbor: {}", self.0) }
}

fn head(out: &mut Vec<u8>, major: u8, n: u64) {
    let m = major << 5;
    if n < 24 {
        out.push(m | n as u8);
    } else if n <= 0xff {
        out.push(m | 24);
        out.push(n as u8);
    } else if n <= 0xffff {
        out.push(m | 25);
        out.extend_from_slice(&(n as u16).to_be_bytes());
    } else if n <= 0xffff_ffff {
        out.push(m | 26);
        out.extend_from_slice(&(n as u32).to_be_bytes());
    } else {
        out.push(m | 27);
        out.extend_from_slice(&n.to_be_bytes());
    }
}

pub fn encode(v: &Value) -> Vec<u8> {
    let mut out = Vec::new();
    enc(v, &mut out);
    out
}

fn enc(v: &Value, out: &mut Vec<u8>) {
    match v {
        Value::U(n) => head(out, 0, *n),
        Value::N(n) => head(out, 1, *n),
        Value::B(b) => { head(out, 2, b.len() as u64); out.extend_from_slice(b); }
        Value::T(s) => { head(out, 3, s.len() as u64); out.extend_from_slice(s.as_bytes()); }
        Value::A(a) => { head(out, 4, a.len() as u64); for x in a { enc(x, out); } }
        Value::M(m) => {
            let mut entries: Vec<(Vec<u8>, Vec<u8>)> = m.iter().map(|(k, v)| (encode(k), encode(v))).collect();
            entries.sort_by(|a, b| a.0.cmp(&b.0));
            head(out, 5, entries.len() as u64);
            for (k, v) in entries { out.extend_from_slice(&k); out.extend_from_slice(&v); }
        }
        Value::Bool(false) => out.push(0xf4),
        Value::Bool(true) => out.push(0xf5),
        Value::Null => out.push(0xf6),
    }
}

/// Max nesting accepted by the decoder (bounded recursion on untrusted input).
const MAX_DEPTH: usize = 32;

/// Strict decode: the whole input must be exactly one canonical item.
pub fn decode(bytes: &[u8]) -> Result<Value, CborError> {
    let mut pos = 0;
    let v = dec(bytes, &mut pos, 0)?;
    if pos != bytes.len() {
        return Err(CborError("trailing bytes".into()));
    }
    if encode(&v) != bytes {
        return Err(CborError("not canonical (RFC 8949 §4.2.1)".into()));
    }
    Ok(v)
}

fn take<'a>(b: &'a [u8], pos: &mut usize, n: usize) -> Result<&'a [u8], CborError> {
    let end = pos.checked_add(n).ok_or_else(|| CborError("length overflow".into()))?;
    if end > b.len() { return Err(CborError("truncated".into())); }
    let s = &b[*pos..end];
    *pos = end;
    Ok(s)
}

fn dec(b: &[u8], pos: &mut usize, depth: usize) -> Result<Value, CborError> {
    if depth > MAX_DEPTH { return Err(CborError("nesting too deep".into())); }
    let ib = take(b, pos, 1)?[0];
    let major = ib >> 5;
    let info = ib & 0x1f;
    if major == 7 {
        return match info {
            20 => Ok(Value::Bool(false)),
            21 => Ok(Value::Bool(true)),
            22 => Ok(Value::Null),
            _ => Err(CborError(format!("unsupported simple/float 0x{ib:02x}"))),
        };
    }
    let n = match info {
        0..=23 => info as u64,
        24 => take(b, pos, 1)?[0] as u64,
        25 => u16::from_be_bytes(take(b, pos, 2)?.try_into().unwrap()) as u64,
        26 => u32::from_be_bytes(take(b, pos, 4)?.try_into().unwrap()) as u64,
        27 => u64::from_be_bytes(take(b, pos, 8)?.try_into().unwrap()),
        _ => return Err(CborError("indefinite length or reserved".into())),
    };
    let len = |n: u64| -> Result<usize, CborError> {
        usize::try_from(n).ok().filter(|l| *l <= b.len()).ok_or_else(|| CborError("length too large".into()))
    };
    Ok(match major {
        0 => Value::U(n),
        1 => Value::N(n),
        2 => Value::B(take(b, pos, len(n)?)?.to_vec()),
        3 => Value::T(String::from_utf8(take(b, pos, len(n)?)?.to_vec()).map_err(|_| CborError("invalid utf-8".into()))?),
        4 => {
            let n = len(n)?;
            let mut a = Vec::with_capacity(n.min(1024));
            for _ in 0..n { a.push(dec(b, pos, depth + 1)?); }
            Value::A(a)
        }
        5 => {
            let n = len(n)?;
            let mut m = Vec::with_capacity(n.min(1024));
            let mut prev: Option<Vec<u8>> = None;
            for _ in 0..n {
                let k = dec(b, pos, depth + 1)?;
                let ke = encode(&k);
                if prev.as_ref().is_some_and(|p: &Vec<u8>| *p >= ke) {
                    return Err(CborError("map keys not strictly ascending (unsorted or duplicate)".into()));
                }
                prev = Some(ke);
                let v = dec(b, pos, depth + 1)?;
                m.push((k, v));
            }
            Value::M(m)
        }
        _ => return Err(CborError(format!("unsupported major type {major}"))),
    })
}

// ---------- typed access helpers for schema-checked maps ----------

/// A decoded map with text keys, consumed field by field. [`Fields::finish`] rejects unknown
/// keys, so every schema is closed (an unexpected field is an error, not silently ignored).
pub struct Fields {
    entries: Vec<(String, Value)>,
    what: &'static str,
}

impl Fields {
    pub fn new(v: Value, what: &'static str) -> Result<Self, CborError> {
        let Value::M(m) = v else { return Err(CborError(format!("{what}: expected map"))) };
        let mut entries = Vec::with_capacity(m.len());
        for (k, v) in m {
            let Value::T(k) = k else { return Err(CborError(format!("{what}: non-text key"))) };
            entries.push((k, v));
        }
        Ok(Fields { entries, what })
    }

    pub fn opt(&mut self, key: &str) -> Option<Value> {
        let i = self.entries.iter().position(|(k, _)| k == key)?;
        Some(self.entries.swap_remove(i).1)
    }

    pub fn req(&mut self, key: &str) -> Result<Value, CborError> {
        self.opt(key).ok_or_else(|| CborError(format!("{}: missing field '{key}'", self.what)))
    }

    pub fn finish(self) -> Result<(), CborError> {
        match self.entries.first() {
            None => Ok(()),
            Some((k, _)) => Err(CborError(format!("{}: unknown field '{k}'", self.what))),
        }
    }
}

impl Value {
    pub fn u(&self) -> Result<u64, CborError> {
        match self { Value::U(n) => Ok(*n), _ => Err(CborError("expected unsigned int".into())) }
    }
    pub fn bytes(&self) -> Result<&[u8], CborError> {
        match self { Value::B(b) => Ok(b), _ => Err(CborError("expected bytes".into())) }
    }
    pub fn fixed<const N: usize>(&self) -> Result<[u8; N], CborError> {
        self.bytes()?.try_into().map_err(|_| CborError(format!("expected {N} bytes")))
    }
    pub fn text(&self) -> Result<&str, CborError> {
        match self { Value::T(s) => Ok(s), _ => Err(CborError("expected text".into())) }
    }
    pub fn array(self) -> Result<Vec<Value>, CborError> {
        match self { Value::A(a) => Ok(a), _ => Err(CborError("expected array".into())) }
    }
    pub fn boolean(&self) -> Result<bool, CborError> {
        match self { Value::Bool(b) => Ok(*b), _ => Err(CborError("expected bool".into())) }
    }
}

/// Builder for maps with text keys.
#[derive(Default)]
pub struct MapB(Vec<(Value, Value)>);

impl MapB {
    pub fn new() -> Self { Self(Vec::new()) }
    pub fn put(mut self, k: &str, v: Value) -> Self { self.0.push((Value::T(k.into()), v)); self }
    pub fn put_opt(self, k: &str, v: Option<Value>) -> Self { match v { Some(v) => self.put(k, v), None => self } }
    pub fn build(self) -> Value { Value::M(self.0) }
}

pub fn b(x: &[u8]) -> Value { Value::B(x.to_vec()) }
pub fn t(x: &str) -> Value { Value::T(x.to_string()) }

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn canonical_roundtrip_and_key_order() {
        // Keys sort by encoded bytes: shorter text first, then bytewise.
        let v = MapB::new().put("bb", Value::U(1)).put("a", Value::U(500)).put("c", b(&[1, 2])).build();
        let e = encode(&v);
        assert_eq!(e, vec![0xa3, 0x61, b'a', 0x19, 0x01, 0xf4, 0x61, b'c', 0x42, 1, 2, 0x62, b'b', b'b', 0x01]);
        assert_eq!(decode(&e).unwrap(), Value::M(vec![
            (t("a"), Value::U(500)), (t("c"), b(&[1, 2])), (t("bb"), Value::U(1))]));
    }

    #[test]
    fn rejects_non_canonical() {
        assert!(decode(&[0x18, 0x05]).is_err()); // non-minimal int
        assert!(decode(&[0xa2, 0x61, b'b', 0x01, 0x61, b'a', 0x02]).is_err()); // unsorted keys
        assert!(decode(&[0xa2, 0x61, b'a', 0x01, 0x61, b'a', 0x02]).is_err()); // duplicate key
        assert!(decode(&[0x5f, 0xff]).is_err()); // indefinite
        assert!(decode(&[0xf9, 0x00, 0x00]).is_err()); // float
        assert!(decode(&[0x01, 0x02]).is_err()); // trailing
    }
}
