//! Item operations (§3.0): closed set of typed E2EE payloads carried in MLS application messages.
//!
//! Plaintext of the MLS application message (canonical CBOR):
//! `{v: 1, opId, h: header, p: payload, sig}`. The header carries the signed fields plus the
//! sender's current `seqHead` (N5: the head lives only inside the encrypted header, never in the MLS
//! authenticated data). Inner signature (R5-03), Ed25519 with the signer device's identityKey over
//! `"ombra/v1/item-op" ‖ 0x00 ‖ teamId[32] ‖ groupDescriptorHash[32] ‖ epoch u64 ‖ rosterVersion u64 ‖
//!  rosterHash[32] ‖ authorMemberId[16] ‖ signerMemberId[16] ‖ signerDeviceId[16] ‖ opType u8 ‖
//!  itemId[16] ‖ rev u64 ‖ seq u64 ‖ recipientMemberId[16] (zeros for space) ‖ SHA-256(canonical payload)`.
//! Payload schemas are closed: unknown op types, kinds or fields are rejected.

use crate::cbor::{self, b, t, Fields, MapB, Value};
use crate::ensure;
use crate::util::{self, e, err, Code, Id16, Res, H32};

pub const ITEM_OP_DOMAIN: &[u8] = b"ombra/v1/item-op";
pub const MAX_OP_BYTES: usize = 64 * 1024;
pub const MAX_NOTE_CHARS: usize = 280;
pub const MAX_CONTEXT_CHARS: usize = 2000;
pub const ZERO16: Id16 = [0u8; 16];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum OpType { Create = 1, Edit = 2, Delete = 3, CollectionAdd = 4, CollectionRemove = 5, Status = 6, Share = 7, SeqHead = 8, SeqSkip = 9, TransferKey = 10 }

impl OpType {
    pub fn from_u8(x: u8) -> Res<OpType> {
        Ok(match x {
            1 => OpType::Create, 2 => OpType::Edit, 3 => OpType::Delete, 4 => OpType::CollectionAdd, 5 => OpType::CollectionRemove,
            6 => OpType::Status, 7 => OpType::Share, 8 => OpType::SeqHead, 9 => OpType::SeqSkip, 10 => OpType::TransferKey,
            _ => return err(Code::OpRejected, format!("unknown operation type {x} (closed list: no replies/threads)")),
        })
    }
    pub fn parse(s: &str) -> Res<OpType> {
        Ok(match s {
            "item.create" => OpType::Create, "item.edit" => OpType::Edit, "item.delete" => OpType::Delete,
            "collection.add" => OpType::CollectionAdd, "collection.remove" => OpType::CollectionRemove,
            "item.status" => OpType::Status, "share" => OpType::Share, "seq.head" => OpType::SeqHead, "seq.skip" => OpType::SeqSkip,
            "transfer.key" => OpType::TransferKey,
            _ => return err(Code::OpRejected, format!("unknown operation '{s}' (closed list: no replies/threads)")),
        })
    }
    pub fn name(self) -> &'static str {
        match self {
            OpType::Create => "item.create", OpType::Edit => "item.edit", OpType::Delete => "item.delete",
            OpType::CollectionAdd => "collection.add", OpType::CollectionRemove => "collection.remove",
            OpType::Status => "item.status", OpType::Share => "share", OpType::SeqHead => "seq.head", OpType::SeqSkip => "seq.skip",
            OpType::TransferKey => "transfer.key",
        }
    }
    /// Control ops (beacons) carry `seq = 0` and do not consume a sequence number.
    pub fn is_control(self) -> bool { matches!(self, OpType::SeqHead | OpType::SeqSkip) }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Header {
    pub team_id: H32,
    pub group_descriptor_hash: H32,
    pub epoch: u64,
    pub roster_version: u64,
    pub roster_hash: H32,
    pub author_member_id: Id16,
    pub signer_member_id: Id16,
    pub signer_device_id: Id16,
    pub op_type: u8,
    pub item_id: Id16,
    pub rev: u64,
    pub seq: u64,
    pub recipient_member_id: Id16,
    /// Sender's current `seq` head in this group (N5). Encrypted, authenticated by MLS, unsigned.
    pub seq_head: u64,
}

impl Header {
    fn to_value(&self) -> Value {
        MapB::new()
            .put("teamId", b(&self.team_id)).put("groupDescriptorHash", b(&self.group_descriptor_hash))
            .put("epoch", Value::U(self.epoch)).put("rosterVersion", Value::U(self.roster_version))
            .put("rosterHash", b(&self.roster_hash)).put("authorMemberId", b(&self.author_member_id))
            .put("signerMemberId", b(&self.signer_member_id)).put("signerDeviceId", b(&self.signer_device_id))
            .put("opType", Value::U(self.op_type as u64)).put("itemId", b(&self.item_id))
            .put("rev", Value::U(self.rev)).put("seq", Value::U(self.seq))
            .put("recipientMemberId", b(&self.recipient_member_id)).put("seqHead", Value::U(self.seq_head))
            .build()
    }
    fn from_value(v: Value) -> Res<Header> {
        let mut f = Fields::new(v, "op header")?;
        let h = Header {
            team_id: f.req("teamId")?.fixed()?, group_descriptor_hash: f.req("groupDescriptorHash")?.fixed()?,
            epoch: f.req("epoch")?.u()?, roster_version: f.req("rosterVersion")?.u()?, roster_hash: f.req("rosterHash")?.fixed()?,
            author_member_id: f.req("authorMemberId")?.fixed()?, signer_member_id: f.req("signerMemberId")?.fixed()?,
            signer_device_id: f.req("signerDeviceId")?.fixed()?,
            op_type: u8::try_from(f.req("opType")?.u()?).map_err(|_| e(Code::OpRejected, "opType out of range"))?,
            item_id: f.req("itemId")?.fixed()?, rev: f.req("rev")?.u()?, seq: f.req("seq")?.u()?,
            recipient_member_id: f.req("recipientMemberId")?.fixed()?, seq_head: f.req("seqHead")?.u()?,
        };
        f.finish()?;
        Ok(h)
    }

    /// The exact bytes signed by the device identityKey (§3.0, R5-03).
    pub fn canonical_bytes(&self, payload_hash: &H32) -> Vec<u8> {
        let mut m = Vec::with_capacity(ITEM_OP_DOMAIN.len() + 1 + 32 * 4 + 16 * 5 + 8 * 4 + 1);
        m.extend_from_slice(ITEM_OP_DOMAIN);
        m.push(0);
        m.extend_from_slice(&self.team_id);
        m.extend_from_slice(&self.group_descriptor_hash);
        m.extend_from_slice(&self.epoch.to_be_bytes());
        m.extend_from_slice(&self.roster_version.to_be_bytes());
        m.extend_from_slice(&self.roster_hash);
        m.extend_from_slice(&self.author_member_id);
        m.extend_from_slice(&self.signer_member_id);
        m.extend_from_slice(&self.signer_device_id);
        m.push(self.op_type);
        m.extend_from_slice(&self.item_id);
        m.extend_from_slice(&self.rev.to_be_bytes());
        m.extend_from_slice(&self.seq.to_be_bytes());
        m.extend_from_slice(&self.recipient_member_id);
        m.extend_from_slice(payload_hash);
        m
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SignedOp {
    pub op_id: Id16,
    pub header: Header,
    /// Canonical payload map (schema-checked by [`Payload::parse`]).
    pub payload: Value,
    pub sig: [u8; 64],
}

impl SignedOp {
    pub fn payload_hash(&self) -> H32 { util::sha256(&[&cbor::encode(&self.payload)]) }
    pub fn signed_bytes(&self) -> Vec<u8> { self.header.canonical_bytes(&self.payload_hash()) }

    pub fn to_bytes(&self) -> Vec<u8> {
        cbor::encode(&MapB::new().put("v", Value::U(1)).put("opId", b(&self.op_id)).put("h", self.header.to_value())
            .put("p", self.payload.clone()).put("sig", b(&self.sig)).build())
    }

    pub fn parse(bytes: &[u8]) -> Res<SignedOp> {
        ensure!(bytes.len() <= MAX_OP_BYTES, Code::OpRejected, "operation larger than 64 KiB (use attachments)");
        let v = cbor::decode(bytes).map_err(|x| e(Code::OpRejected, x.0))?;
        let mut f = Fields::new(v, "item op")?;
        ensure!(f.req("v")?.u()? == 1, Code::OpRejected, "unsupported item-op version");
        let op = SignedOp { op_id: f.req("opId")?.fixed()?, header: Header::from_value(f.req("h")?)?, payload: f.req("p")?, sig: f.req("sig")?.fixed()? };
        f.finish()?;
        Ok(op)
    }

    pub fn op_type(&self) -> Res<OpType> { OpType::from_u8(self.header.op_type) }

    pub fn verify_sig(&self, identity_key: &[u8]) -> bool { util::ed25519_verify(identity_key, &self.signed_bytes(), &self.sig) }

    pub fn item_key(&self) -> ItemKey { ItemKey { author: self.header.author_member_id, item_id: self.header.item_id } }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ItemKey { pub author: Id16, pub item_id: Id16 }

impl ItemKey {
    pub fn key(&self) -> String { format!("{}{}", util::hex(&self.author), util::hex(&self.item_id)) }
    fn to_value(&self) -> Value { Value::A(vec![b(&self.author), b(&self.item_id)]) }
    fn from_value(v: Value) -> Res<ItemKey> {
        let a = v.array()?;
        ensure!(a.len() == 2, Code::OpRejected, "itemKey must be [authorMemberId, itemId]");
        Ok(ItemKey { author: a[0].fixed()?, item_id: a[1].fixed()? })
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind { Mark, File, Skill, Link, Snapshot, Edit, Collection }

impl Kind {
    pub fn parse(s: &str) -> Res<Kind> {
        Ok(match s { "mark" => Kind::Mark, "file" => Kind::File, "skill" => Kind::Skill, "link" => Kind::Link, "snapshot" => Kind::Snapshot,
            "edit" => Kind::Edit, "collection" => Kind::Collection,
            _ => return err(Code::OpRejected, format!("unknown item kind '{s}'")) })
    }
    pub fn as_str(self) -> &'static str {
        match self { Kind::Mark => "mark", Kind::File => "file", Kind::Skill => "skill", Kind::Link => "link", Kind::Snapshot => "snapshot",
            Kind::Edit => "edit", Kind::Collection => "collection" }
    }
    /// Resources (ред. 10): everything but marks — `seen|accepted|declined`, the first final status is final.
    pub fn is_resource(self) -> bool { self != Kind::Mark }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Audience { Space(Vec<u8>), Member(Id16) }

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StatusKind { Seen, Done, Dismissed, Accepted, Declined }

impl StatusKind {
    pub fn parse(s: &str) -> Res<StatusKind> {
        Ok(match s { "seen" => StatusKind::Seen, "done" => StatusKind::Done, "dismissed" => StatusKind::Dismissed,
            "accepted" => StatusKind::Accepted, "declined" => StatusKind::Declined,
            x => return err(Code::OpRejected, format!("unknown status '{x}'")) })
    }
    pub fn as_str(self) -> &'static str {
        match self { StatusKind::Seen => "seen", StatusKind::Done => "done", StatusKind::Dismissed => "dismissed",
            StatusKind::Accepted => "accepted", StatusKind::Declined => "declined" }
    }
    /// Marks: `seen|done|dismissed`; resources: `seen|accepted|declined` (§3.0 ред. 10).
    pub fn allowed_for(self, k: Kind) -> bool {
        match self { StatusKind::Seen => true, StatusKind::Done | StatusKind::Dismissed => !k.is_resource(), StatusKind::Accepted | StatusKind::Declined => k.is_resource() }
    }
}

/// Content fields that `item.create` sets and `item.edit` may replace (all canonical CBOR).
#[derive(Clone, Debug, PartialEq, Eq, Default)]
pub struct Content {
    pub anchor: Option<Value>,
    pub context: Option<Value>,
    pub body: Option<Value>,
    pub attachments: Option<Value>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Payload {
    Create { kind: Kind, audience: Audience, created_at: u64, content: Content },
    Edit { base_rev: u64, delta: Content },
    Delete,
    CollectionAdd(ItemKey),
    CollectionRemove(ItemKey),
    Status { status: StatusKind, note: Option<String> },
    Share { share_id: Id16, target_member: Id16, target_device: Id16, scopes: Vec<Vec<u8>>, count: u64, sha256: H32 },
    SeqHead { last_seq: u64 },
    SeqSkip { from: u64, to: u64, device: Id16 },
    TransferKey { transfer_id: Id16, target_device: Id16, size: u64, sha256: H32, key: H32 },
}

fn chars(s: &str) -> usize { s.chars().count() }

fn text_fields(v: Value, what: &'static str, required: &[&str], optional: &[&str]) -> Res<Value> {
    let mut f = Fields::new(v.clone(), what)?;
    for k in required { f.req(k)?; }
    for k in optional { f.opt(k); }
    f.finish()?;
    Ok(v)
}

fn parse_anchor(v: Value) -> Res<Value> {
    let mut f = Fields::new(v.clone(), "anchor")?;
    match (f.opt("web"), f.opt("code")) {
        (Some(w), None) => { text_fields(w, "anchor.web", &["url", "selector", "textQuote"], &["viewport"])?; }
        (None, Some(c)) => { text_fields(c, "anchor.code", &["repoFingerprint", "commit", "path", "range"], &["excerpt"])?; }
        (None, None) => match f.opt("doc") {
            Some(d) => { text_fields(d, "anchor.doc", &["fileRef", "locator"], &["excerpt"])?; }
            None => return err(Code::OpRejected, "anchor must be exactly one of web|code|doc"),
        },
        _ => return err(Code::OpRejected, "anchor must be exactly one of web|code|doc"),
    }
    f.finish()?;
    Ok(v)
}

fn parse_context(v: Value) -> Res<Value> {
    let mut f = Fields::new(v.clone(), "context")?;
    if let Some(tx) = f.opt("text") {
        ensure!(chars(tx.text()?) <= MAX_CONTEXT_CHARS, Code::OpRejected, "context text longer than {MAX_CONTEXT_CHARS} characters");
    }
    f.opt("selection");
    if let Some(br) = f.opt("blobRefs") { br.array()?; }
    f.finish()?;
    Ok(v)
}

pub fn parse_body(kind: Kind, v: Option<Value>) -> Res<Option<Value>> {
    match (kind, v) {
        (Kind::Mark, None) => Ok(None),
        (Kind::Mark, Some(_)) => err(Code::OpRejected, "mark has no body"),
        (_, None) => err(Code::OpRejected, format!("{} requires a body", kind.as_str())),
        (Kind::File, Some(v)) => text_fields(v, "file", &["name", "blobRef", "size", "sha256"], &["mime"]).map(Some),
        (Kind::Skill, Some(v)) => text_fields(v, "skill", &["manifest", "blobRef", "version"], &["name", "source", "size", "sha256"]).map(Some),
        (Kind::Link, Some(v)) => text_fields(v, "link", &["url"], &["title"]).map(Some),
        (Kind::Snapshot, Some(v)) => text_fields(v, "snapshot", &["blobRef"], &["size", "sha256", "width", "height", "sourceUrl"]).map(Some),
        (Kind::Edit, Some(v)) => text_fields(v, "edit", &["baseCommit", "patch"], &["repoFingerprint", "files", "size", "sha256"]).map(Some),
        (Kind::Collection, Some(v)) => {
            let mut f = Fields::new(v.clone(), "collection")?;
            for k in f.req("itemKeys")?.array()? { ItemKey::from_value(k)?; }
            collection_title(&mut f)?;
            f.finish()?;
            Ok(Some(v))
        }
    }
}

/// A collection's optional name (a shared tab group's label), at most this many characters.
pub const MAX_COLLECTION_TITLE_CHARS: usize = 200;

/// The collection body's optional `title`, validated (text, 1–200 characters).
pub fn collection_title(f: &mut Fields) -> Res<Option<Value>> {
    let Some(t) = f.opt("title") else { return Ok(None) };
    let n = chars(t.text()?);
    ensure!(n > 0 && n <= MAX_COLLECTION_TITLE_CHARS, Code::OpRejected, "collection title must be 1–200 characters");
    Ok(Some(t))
}

fn parse_content(f: &mut Fields, kind: Option<Kind>) -> Res<Content> {
    Ok(Content {
        anchor: f.opt("anchor").map(parse_anchor).transpose()?,
        context: f.opt("context").map(parse_context).transpose()?,
        body: match kind { Some(k) => parse_body(k, f.opt("body"))?, None => f.opt("body") },
        attachments: f.opt("attachments").map(|a| -> Res<Value> {
            for x in a.clone().array()? { text_fields(x, "attachment", &["blobRef", "key"], &[])?; }
            Ok(a)
        }).transpose()?,
    })
}

impl Payload {
    pub fn parse(op: OpType, v: &Value) -> Res<Payload> {
        let mut f = Fields::new(v.clone(), "payload").map_err(|x| e(Code::OpRejected, x.0))?;
        let p = match op {
            OpType::Create => {
                let kind = Kind::parse(f.req("kind")?.text()?)?;
                let mut af = Fields::new(f.req("audience")?, "audience")?;
                let audience = match (af.opt("space"), af.opt("member")) {
                    (Some(s), None) => Audience::Space(s.bytes()?.to_vec()),
                    (None, Some(m)) => Audience::Member(m.fixed()?),
                    _ => return err(Code::OpRejected, "audience must be exactly one of space|member"),
                };
                af.finish()?;
                let created_at = f.req("createdAt")?.u()?;
                let content = parse_content(&mut f, Some(kind))?;
                if kind == Kind::Mark { ensure!(content.anchor.is_some(), Code::OpRejected, "mark requires an anchor"); }
                Payload::Create { kind, audience, created_at, content }
            }
            OpType::Edit => {
                let base_rev = f.req("baseRev")?.u()?;
                let mut df = Fields::new(f.req("delta")?, "delta")?;
                let delta = parse_content(&mut df, None)?;
                df.finish()?;
                ensure!(delta != Content::default(), Code::OpRejected, "empty edit delta");
                Payload::Edit { base_rev, delta }
            }
            OpType::Delete => Payload::Delete,
            OpType::CollectionAdd => Payload::CollectionAdd(ItemKey::from_value(f.req("itemKey")?)?),
            OpType::CollectionRemove => Payload::CollectionRemove(ItemKey::from_value(f.req("itemKey")?)?),
            OpType::Status => {
                let status = StatusKind::parse(f.req("status")?.text()?)?;
                let note = f.opt("note").map(|n| n.text().map(str::to_string)).transpose()?;
                if let Some(n) = &note { ensure!(chars(n) <= MAX_NOTE_CHARS, Code::OpRejected, "note longer than {MAX_NOTE_CHARS} characters"); }
                Payload::Status { status, note }
            }
            OpType::Share => Payload::Share {
                share_id: f.req("shareId")?.fixed()?, target_member: f.req("targetMemberId")?.fixed()?,
                target_device: f.req("targetDeviceId")?.fixed()?,
                scopes: f.req("scopes")?.array()?.iter().map(|s| s.bytes().map(|x| x.to_vec())).collect::<Result<_, _>>()?,
                count: f.req("count")?.u()?, sha256: f.req("sha256")?.fixed()?,
            },
            OpType::SeqHead => Payload::SeqHead { last_seq: f.req("lastSeq")?.u()? },
            OpType::SeqSkip => {
                let (from, to) = (f.req("from")?.u()?, f.req("to")?.u()?);
                ensure!(from >= 1 && from <= to && to - from < 1_000_000, Code::OpRejected, "bad seq.skip range");
                Payload::SeqSkip { from, to, device: f.req("deviceId")?.fixed()? }
            }
            OpType::TransferKey => Payload::TransferKey {
                transfer_id: f.req("transferId")?.fixed()?, target_device: f.req("targetDeviceId")?.fixed()?,
                size: f.req("size")?.u()?, sha256: f.req("sha256")?.fixed()?, key: f.req("key")?.fixed()?,
            },
        };
        f.finish().map_err(|x| e(Code::OpRejected, x.0))?;
        Ok(p)
    }
}

pub fn content_value(c: &Content, base: MapB) -> MapB {
    base.put_opt("anchor", c.anchor.clone()).put_opt("context", c.context.clone())
        .put_opt("body", c.body.clone()).put_opt("attachments", c.attachments.clone())
}

pub fn audience_value(a: &Audience) -> Value {
    match a { Audience::Space(s) => MapB::new().put("space", b(s)).build(), Audience::Member(m) => MapB::new().put("member", b(m)).build() }
}

pub fn item_key_value(k: &ItemKey) -> Value { k.to_value() }
pub fn item_key_from(v: Value) -> Res<ItemKey> { ItemKey::from_value(v) }
pub fn kind_value(k: Kind) -> Value { t(k.as_str()) }
