//! JSON command layer behind the C ABI (`verify_and_apply(state, input) -> result`, §7).
//!
//! Request: `{"op": "<name>", ...args}`; bytes are lowercase hex strings. Response:
//! `{"ok": true, "result": {...}}` or `{"ok": false, "code": "<code>", "error": "<text>"}` with the
//! codes of `util::Code`. Stateful ops run in one transaction of the `Session` (rejections leave the
//! state untouched); stateless ops (`call_stateless`) are the server's "public verification" mode.

use serde::de::DeserializeOwned;
use serde_json::{json, Map, Value as J};

use crate::cbor::{self, MapB, Value};
use crate::cert::DeviceCert;
use crate::descriptor::{Descriptor, GroupType};
use crate::ensure;
use crate::formats::{self, BindDevice, Invite, RemovalNotice};
use crate::pairing;
use crate::heartbeat::Heartbeat;
use crate::itemop::{self, Audience, OpType, Payload, SignedOp, Header};
use crate::mls::{self, CommitSpec, OpSpec};
use crate::replica::{self, ApplyCtx};
use crate::roster::{self, AccessEntry, Chain, MemberEntry, DeviceEntry, Ownership, Projection, Recovery, RecoveryProposal, Role, RosterUpdate, Sig, Pending, Cause, Level};
use crate::store::Db;
use crate::outbox;
use crate::state::{InviteSecret, PendingJoin, Session, State, TeamState, Provider};
use crate::util::{self, e, err, hex, Code, Error, Id16, Res, H32};

pub struct Args(Map<String, J>);

impl Args {
    pub fn new(j: J) -> Res<Args> {
        match j { J::Object(m) => Ok(Args(m)), _ => err(Code::InvalidInput, "request must be a JSON object") }
    }
    pub fn raw(&self, k: &str) -> Option<&J> { self.0.get(k).filter(|v| !v.is_null()) }
    pub fn req(&self, k: &str) -> Res<&J> { self.raw(k).ok_or_else(|| e(Code::InvalidInput, format!("missing '{k}'"))) }
    pub fn str(&self, k: &str) -> Res<&str> { self.req(k)?.as_str().ok_or_else(|| e(Code::InvalidInput, format!("'{k}' must be a string"))) }
    pub fn opt_str(&self, k: &str) -> Option<&str> { self.raw(k).and_then(|v| v.as_str()) }
    pub fn bytes(&self, k: &str) -> Res<Vec<u8>> { util::unhex(self.str(k)?) }
    pub fn opt_bytes(&self, k: &str) -> Res<Option<Vec<u8>>> { self.opt_str(k).map(util::unhex).transpose() }
    pub fn id(&self, k: &str) -> Res<Id16> { util::id16(&self.bytes(k)?) }
    pub fn opt_id(&self, k: &str) -> Res<Option<Id16>> { self.opt_bytes(k)?.map(|b| util::id16(&b)).transpose() }
    pub fn h(&self, k: &str) -> Res<H32> { util::h32(&self.bytes(k)?) }
    pub fn u(&self, k: &str) -> Res<u64> { self.req(k)?.as_u64().ok_or_else(|| e(Code::InvalidInput, format!("'{k}' must be an unsigned integer"))) }
    pub fn opt_u(&self, k: &str) -> Option<u64> { self.raw(k).and_then(|v| v.as_u64()) }
    pub fn flag(&self, k: &str) -> bool { self.raw(k).and_then(|v| v.as_bool()).unwrap_or(false) }
    pub fn list(&self, k: &str) -> Vec<J> { self.raw(k).and_then(|v| v.as_array()).cloned().unwrap_or_default() }
    pub fn hex_list(&self, k: &str) -> Res<Vec<Vec<u8>>> {
        self.list(k).iter().map(|v| v.as_str().ok_or_else(|| e(Code::InvalidInput, format!("'{k}' must be hex strings"))).and_then(util::unhex)).collect()
    }
    pub fn typed<T: DeserializeOwned>(&self, k: &str) -> Res<T> {
        serde_json::from_value(self.req(k)?.clone()).map_err(|x| e(Code::InvalidInput, format!("'{k}': {x}")))
    }
    pub fn opt_typed<T: DeserializeOwned>(&self, k: &str) -> Res<Option<T>> {
        self.raw(k).map(|v| serde_json::from_value(v.clone()).map_err(|x| e(Code::InvalidInput, format!("'{k}': {x}")))).transpose()
    }
    pub fn sub(&self, k: &str) -> Res<Args> { Args::new(self.req(k)?.clone()) }
}

pub fn ok(result: J) -> J { json!({"ok": true, "result": result}) }
pub fn fail(x: &Error) -> J { json!({"ok": false, "code": x.code.as_str(), "error": x.msg}) }

fn proj_json(p: &Projection) -> J { serde_json::to_value(p).expect("projection serializes") }

// ======================= stateless (public verification) =======================

pub fn call_stateless(req: J) -> J {
    match stateless(req) { Ok(r) => ok(r), Err(x) => fail(&x) }
}

fn stateless(req: J) -> Res<J> {
    let a = Args::new(req)?;
    let op = a.str("op")?.to_string();
    Ok(match op.as_str() {
        "ed25519.verify" => json!({"valid": util::ed25519_verify(&a.bytes("publicKey")?, &a.bytes("message")?, &a.bytes("signature")?)}),
        "ed25519.keygen" => { let sk = util::random::<32>(); json!({"secretKey": hex(&sk), "publicKey": hex(&util::ed25519_public(&sk))}) }
        "envelope.verify" => json!({"valid": formats::verify_envelope(&a.bytes("body")?, &a.bytes("signature")?, &a.bytes("identityKey")?)}),
        "envelope.message" => json!({"message": hex(&formats::envelope_message(&a.bytes("body")?)?)}),
        "hello.message" => {
            let nonce: [u8; 32] = util::h32(&a.bytes("nonce")?)?;
            json!({"message": hex(&formats::hello_message(&a.id("serverId")?, &nonce, &a.id("deviceId")?, a.u("time")?))})
        }
        "hello.verify" => {
            let nonce: [u8; 32] = util::h32(&a.bytes("nonce")?)?;
            let m = formats::hello_message(&a.id("serverId")?, &nonce, &a.id("deviceId")?, a.u("time")?);
            json!({"valid": util::ed25519_verify(&a.bytes("identityKey")?, &m, &a.bytes("signature")?)})
        }
        "cbor.check" => json!({"canonical": cbor::decode(&a.bytes("bytes")?).is_ok()}),
        "invite.keys" => {
            let k = formats::invite_keys(&a.bytes("k")?, &a.id("inviteId")?)?;
            json!({"kSrv": hex(&k.k_srv), "kAdm": hex(&k.k_adm), "verifier": hex(&k.verifier)})
        }
        "invite.bind" | "invite.verify_bind" => {
            let devs = bind_devices(&a)?;
            let (iid, mid, kadm) = (a.id("inviteId")?, a.id("memberId")?, a.bytes("kAdm")?);
            if op == "invite.bind" {
                json!({"bind": hex(&formats::bind(&kadm, &iid, &mid, &devs)?), "message": hex(&formats::bind_message(&iid, &mid, &devs)?)})
            } else {
                json!({"valid": formats::verify_bind(&kadm, &iid, &mid, &devs, &a.bytes("bind")?)?})
            }
        }
        "descriptor.group_id" => {
            let d: Descriptor = a.typed("descriptor")?;
            d.validate()?;
            json!({"groupId": hex(&d.group_id()), "canonical": hex(&d.canonical())})
        }
        "itemop.canonical" => {
            let h = header_from(&a.sub("header")?)?;
            let p = replica::json_cbor(a.req("payload")?)?;
            let ph = util::sha256(&[&cbor::encode(&p)]);
            json!({"canonical": hex(&h.canonical_bytes(&ph)), "payloadHash": hex(&ph), "payloadCbor": hex(&cbor::encode(&p))})
        }
        "itemop.parse" => {
            // The request key "op" is the command name, so the SignedOp bytes travel as "signedOp".
            let op = SignedOp::parse(&a.bytes("signedOp")?)?;
            let ty = op.op_type()?;
            Payload::parse(ty, &op.payload)?;
            json!({"opType": ty.name(), "opId": hex(&op.op_id), "signed": hex(&op.signed_bytes()), "payload": replica::cbor_json(&op.payload)})
        }
        "heartbeat.canonical" => {
            let hb = hb_from(&a)?;
            json!({"signed": hex(&hb.signed_bytes()?)})
        }
        "removal.canonical" => {
            let n = RemovalNotice { team_id: a.h("teamId")?, member_id: a.id("memberId")?, roster_version: a.u("rosterVersion")?, time: a.u("time")?, signer_device_id: a.id("signerDeviceId")? };
            json!({"signed": hex(&n.signed_bytes())})
        }
        "removal.inspect" => {
            // Server side: routing fields of a RemovalNotice; the signature is checked against the signer's key.
            let (n, signed, sig) = RemovalNotice::parse(&a.bytes("notice")?)?;
            json!({"teamId": hex(&n.team_id), "memberId": hex(&n.member_id), "rosterVersion": n.roster_version, "time": n.time,
                "signerDeviceId": hex(&n.signer_device_id), "signed": hex(&signed), "signature": hex(&sig)})
        }
        "heartbeat.inspect" => {
            // Server side (§6.1 POST …/heartbeat): fields + signed bytes; epochProofs only clients can check.
            let (hb, signed, sig) = crate::heartbeat::Heartbeat::parse(&a.bytes("heartbeat")?)?;
            json!({"teamId": hex(&hb.team_id), "rosterVersion": hb.roster_version, "rosterHash": hex(&hb.roster_hash), "time": hb.time,
                "signerDeviceId": hex(&hb.signer_device_id), "groups": hb.groups.len(), "signed": hex(&signed), "signature": hex(&sig)})
        }
        "ownership.bytes" => {
            // Signing inputs of an OwnershipOffer: the server checks the offer signature of the owner device.
            let o: roster::OwnershipOffer = a.typed("offer")?;
            json!({"offer": hex(&o.offer_bytes()), "accept": hex(&o.accept_bytes())})
        }
        "invite.parse" => {
            let inv = Invite::parse(&a.bytes("signedInvite")?)?;
            json!({"inviteId": hex(&inv.invite_id), "teamId": hex(&inv.team_id), "rosterHead": {"version": inv.head_version, "hash": hex(&inv.head_hash)},
                "role": inv.role, "expiresAt": inv.expires_at, "maxUses": inv.max_uses, "verifier": hex(&inv.verifier),
                "inviterDeviceId": hex(&inv.inviter_device_id), "inviterFingerprint": hex(&inv.fingerprint()), "valid": inv.verify()})
        }
        "pair.commit" => json!({"commitment": hex(&pairing::commitment(&party(&a)?))}),
        "pair.sas" => {
            let (ex, nw) = (party(&a.sub("existing")?)?, party(&a.sub("new")?)?);
            match pairing::sas(&ex, &a.bytes("commitment")?, &nw) {
                Some(w) => json!({"valid": true, "words": w}),
                None => json!({"valid": false, "reason": "the revealed nonce/keys do not match the commitment"}),
            }
        }
        "pair.code" => json!({"code": pairing::manual_code(&party(&a)?)}),
        "pair.code_check" => json!({"matches": pairing::code_matches(&party(&a)?, a.str("code")?)}),
        "pair.qr" => json!({"payload": hex(&pairing::qr_payload(&party(&a)?))}),
        "pair.qr_parse" => {
            let p = pairing::parse_qr(&a.bytes("payload")?)?;
            json!({"deviceId": hex(&p.device_id), "identityKey": hex(&p.identity_key), "mlsSignatureKey": hex(&p.mls_key), "nonce": hex(&p.nonce)})
        }
        "roster.canonical" => {
            let p: Projection = a.typed("projection")?;
            json!({"canonical": hex(&p.canonical()), "hash": hex(&p.hash()), "signed": hex(&p.signed_bytes())})
        }
        "roster.parse" => {
            let u = RosterUpdate::parse(&a.bytes("update")?)?;
            json!({"projection": proj_json(&u.projection), "hash": hex(&u.projection.hash()), "signers": u.signatures.iter().map(|s| hex(&s.device_id)).collect::<Vec<_>>()})
        }
        "roster.append" => {
            // Server-side chain verification. chainState is the opaque JSON returned last time.
            let server_id = a.id("serverId")?;
            let update = a.bytes("update")?;
            let mut chain = match a.opt_str("chainState") {
                None => Chain::genesis(server_id, &update, a.raw("expectedTeamId").map(|_| a.h("expectedTeamId")).transpose()?)?,
                Some(s) => {
                    let mut c: Chain = serde_json::from_slice(&util::unhex(s)?).map_err(|x| e(Code::InvalidInput, format!("chainState: {x}")))?;
                    c.rehydrate()?;
                    ensure!(c.server_id == server_id, Code::InvalidInput, "chainState of another server");
                    match c.append(&update, a.u("now")?) {
                        Ok(_) => {}
                        Err(x) if x.code == Code::Equivocation => {
                            let version = c.equivocation.as_ref().map(|q| q.version);
                            return Ok(json!({"equivocation": true, "version": version, "chainState": hex(&serde_json::to_vec(&c).unwrap()), "error": x.msg}));
                        }
                        Err(x) => return Err(x),
                    }
                    c
                }
            };
            chain.rehydrate()?;
            let head = chain.head();
            json!({"chainState": hex(&serde_json::to_vec(&chain).unwrap()), "teamId": hex(&chain.team_id), "version": head.version,
                "hash": hex(&chain.hash_of(head.version).unwrap()), "projection": proj_json(head), "equivocation": false})
        }
        "roster.resolve" => {
            // Server-side: apply the owner's signed EquivocationResolution to the frozen chain state.
            let server_id = a.id("serverId")?;
            let mut c: Chain = serde_json::from_slice(&a.bytes("chainState")?).map_err(|x| e(Code::InvalidInput, format!("chainState: {x}")))?;
            c.rehydrate()?;
            ensure!(c.server_id == server_id, Code::InvalidInput, "chainState of another server");
            let version = roster::Resolution::parse(&a.bytes("resolution")?)?.version;
            let r = c.resolve(&a.bytes("resolution")?, a.u("now")?)?;
            let head = c.head();
            json!({"chainState": hex(&serde_json::to_vec(&c).unwrap()), "teamId": hex(&c.team_id), "version": head.version,
                "hash": hex(&c.hash_of(head.version).unwrap()), "projection": proj_json(head), "equivocation": false,
                "switched": r.switched, "resolvedVersion": version})
        }
        "recovery.sign_proposal" => {
            let sk = util::h32(&a.bytes("recoverySecretKey")?)?;
            let p: RecoveryProposal = a.typed("proposal")?;
            json!({"sig": hex(&util::ed25519_sign(&sk, &p.signed_bytes()))})
        }
        "recovery.sign_key_change" => {
            let sk = util::h32(&a.bytes("recoverySecretKey")?)?;
            let r: Recovery = a.typed("recovery")?;
            json!({"sig": hex(&util::ed25519_sign(&sk, &roster::recovery_change_bytes(&a.h("teamId")?, a.u("version")?, &r)))})
        }
        "roster.private_commit" => json!({"privateCommit": hex(&private_commit(&a)?)}),
        "roster.verify_chain" => {
            let chain = verify_chain(&a, a.raw("teamId").map(|_| a.h("teamId")).transpose()?)?;
            let v = chain.head_version();
            json!({"teamId": hex(&chain.team_id), "version": v, "hash": hex(&chain.hash_of(v).expect("head is in chain"))})
        }
        "itemop.verify" => {
            let chain = verify_chain(&a, Some(a.h("teamId")?))?;
            let op = SignedOp::parse(&a.bytes("signedOp")?)?;
            let ty = op.op_type()?;
            Payload::parse(ty, &op.payload)?;
            replica::verify_at_version(&chain, &op)?;
            json!({"valid": true, "opType": ty.name(), "rosterVersion": op.header.roster_version, "signerDeviceId": hex(&op.header.signer_device_id)})
        }
        "cert.parse" => {
            let c = DeviceCert::parse(&a.bytes("cert")?)?;
            json!({"cert": serde_json::to_value(&c).unwrap(), "hash": hex(&c.hash()), "signed": hex(&c.signed_bytes())})
        }
        "message.inspect" => inspect(&a.bytes("message")?)?,
        "keypackage.inspect" => {
            use openmls::prelude::{tls_codec::Deserialize as _, *};
            let bytes = a.bytes("keyPackage")?;
            let crypto = openmls_rust_crypto::RustCrypto::default();
            let kp = KeyPackageIn::tls_deserialize_exact(&bytes).map_err(|x| e(Code::InvalidInput, format!("{x:?}")))?
                .validate(&crypto, ProtocolVersion::Mls10).map_err(|x| e(Code::InvalidInput, format!("invalid KeyPackage: {x:?}")))?;
            let ln = kp.leaf_node();
            json!({"deviceId": hex(ln.credential().serialized_content()), "mlsSignatureKey": hex(ln.signature_key().as_slice()),
                "notBefore": kp.life_time().not_before(), "notAfter": kp.life_time().not_after(), "lastResort": kp.last_resort(),
                "kpRef": hex(kp.hash_ref(&crypto).map_err(|x| e(Code::Internal, format!("{x:?}")))?.as_slice())})
        }
        "pgroup.init" | "pgroup.commit" => {
            let mut chain: Chain = serde_json::from_slice(&a.bytes("chainState")?).map_err(|x| e(Code::InvalidInput, format!("chainState: {x}")))?;
            chain.rehydrate()?;
            let (st, mut out) = if op == "pgroup.init" {
                crate::public::init(&chain, &a.bytes("groupInfo")?)?
            } else {
                let st: crate::public::PgState = serde_json::from_slice(&a.bytes("groupState")?).map_err(|x| e(Code::InvalidInput, format!("groupState: {x}")))?;
                crate::public::commit(&chain, st, &a.bytes("commit")?)?
            };
            out["groupState"] = json!(hex(&serde_json::to_vec(&st).unwrap()));
            out
        }
        _ => return err(Code::InvalidInput, format!("unknown stateless op '{op}'")),
    })
}

/// `privateCommit = SHA-256(salt[32] ‖ canonical(privatePart))` (§4.1, R2-07).
fn private_commit(a: &Args) -> Res<H32> {
    let salt = util::h32(&a.bytes("salt")?)?;
    let part = replica::json_cbor(a.req("privatePart")?)?;
    Ok(util::sha256(&[&salt, &cbor::encode(&part)]))
}

/// Verify a whole roster chain `updates` (genesis first) for `serverId` at time `now`;
/// `team_id` is the out-of-band genesis hash, if known.
fn verify_chain(a: &Args, team_id: Option<H32>) -> Res<Chain> {
    let updates = a.hex_list("updates")?;
    let (genesis, rest) = updates.split_first().ok_or_else(|| e(Code::InvalidInput, "'updates' must start with the genesis"))?;
    let now = a.u("now")?;
    let mut chain = Chain::genesis(a.id("serverId")?, genesis, team_id)?;
    for u in rest { chain.append(u, now)?; }
    Ok(chain)
}

fn bind_devices(a: &Args) -> Res<Vec<BindDevice>> {
    a.list("devices").into_iter().map(|d| {
        let d = Args::new(d)?;
        Ok(BindDevice { device_id: d.id("deviceId")?, identity_key: util::h32(&d.bytes("identityKey")?)?,
            mls_signature_key: util::h32(&d.bytes("mlsSignatureKey")?)?, key_package: d.bytes("keyPackage")? })
    }).collect()
}

/// A pairing party `{deviceId, identityKey, mlsSignatureKey, nonce}` (§2.3).
fn party(a: &Args) -> Res<pairing::Party> {
    Ok(pairing::Party { device_id: a.id("deviceId")?, identity_key: a.h("identityKey")?, mls_key: a.h("mlsSignatureKey")?, nonce: a.h("nonce")? })
}

pub(crate) fn header_from(h: &Args) -> Res<Header> {
    Ok(Header {
        team_id: h.h("teamId")?, group_descriptor_hash: h.h("groupDescriptorHash")?, epoch: h.u("epoch")?,
        roster_version: h.u("rosterVersion")?, roster_hash: h.h("rosterHash")?, author_member_id: h.id("authorMemberId")?,
        signer_member_id: h.id("signerMemberId")?, signer_device_id: h.id("signerDeviceId")?,
        op_type: u8::try_from(h.u("opType")?).map_err(|_| e(Code::InvalidInput, "opType"))?, item_id: h.id("itemId")?,
        rev: h.u("rev")?, seq: h.u("seq")?, recipient_member_id: h.opt_id("recipientMemberId")?.unwrap_or(itemop::ZERO16),
        seq_head: h.opt_u("seqHead").unwrap_or(0),
    })
}

fn hb_from(a: &Args) -> Res<Heartbeat> {
    let groups = a.list("groups").into_iter().map(|g| {
        let g = Args::new(g)?;
        Ok(crate::heartbeat::HbGroup { group_id: g.bytes("groupId")?, epoch: g.u("epoch")?, epoch_proof: g.h("epochProof")? })
    }).collect::<Res<Vec<_>>>()?;
    Ok(Heartbeat { team_id: a.h("teamId")?, roster_version: a.u("rosterVersion")?, roster_hash: a.h("rosterHash")?, groups,
        time: a.u("time")?, signer_device_id: a.id("signerDeviceId")? })
}

fn inspect(bytes: &[u8]) -> Res<J> {
    use openmls::prelude::{tls_codec::Deserialize as _, *};
    let mi = MlsMessageIn::tls_deserialize_exact(bytes).map_err(|x| e(Code::InvalidInput, format!("{x:?}")))?;
    let wf = format!("{:?}", mi.wire_format());
    match mi.extract() {
        MlsMessageBodyIn::PrivateMessage(pm) => {
            let pm: ProtocolMessage = pm.into();
            let aad = match &pm { ProtocolMessage::PrivateMessage(x) => x.aad().to_vec(), _ => vec![] };
            Ok(json!({"wireFormat": wf, "groupId": hex(pm.group_id().as_slice()), "epoch": pm.epoch().as_u64(),
                "contentType": format!("{:?}", pm.content_type()), "aad": hex(&aad)}))
        }
        MlsMessageBodyIn::PublicMessage(pm) => {
            let pm: ProtocolMessage = pm.into();
            let sender = match &pm { ProtocolMessage::PublicMessage(x) => format!("{:?}", x.sender()), _ => String::new() };
            Ok(json!({"wireFormat": wf, "groupId": hex(pm.group_id().as_slice()), "epoch": pm.epoch().as_u64(),
                "contentType": format!("{:?}", pm.content_type()), "sender": sender}))
        }
        _ => Ok(json!({"wireFormat": wf})),
    }
}

// ======================= stateful =======================

pub fn call_stateful(s: &mut Session, req: J) -> J {
    let a = match Args::new(req) { Ok(a) => a, Err(x) => return fail(&x) };
    let op = match a.str("op") { Ok(o) => o.to_string(), Err(x) => return fail(&x) };
    if op == "state.rekey" {
        // D-5: re-encrypt the whole state under a new key from the OS key store, so copies of the file sealed with
        // the old key (snapshots, backups) become unreadable once the caller destroys the old key.
        return match a.h("key").and_then(|k| s.rekey(k)) { Ok(()) => ok(json!({"rekeyed": true})), Err(x) => fail(&x) };
    }
    match op.as_str() {
        "team.unlock" => return match a.h("teamId").and_then(|t| Ok((t, a.h("key")?))).and_then(|(t, k)| s.unlock(&t, k)) {
            Ok(()) => ok(json!({"unlocked": true})), Err(x) => fail(&x) },
        "state.teams" => return ok(json!({"teams": s.teams().into_iter().map(|(t, u)| json!({"teamId": t, "unlocked": u})).collect::<Vec<_>>()})),
        _ => {}
    }
    let r = s.transact(|st, p, db| stateful(st, p, db, &op, &a));
    if op == "team.forget" && r.is_ok() { s.checkpoint(); }
    if op != "group.process" { return match r { Ok(r) => ok(r), Err(x) => fail(&x) }; }
    // Ack rules (design-sync-decision §4): committed → ack; final rejection → ack + security log (+ receipt, so a
    // redelivery is acked without decrypting); transient (storage, internal, need-roster, locked) → no ack, retried.
    match r {
        Ok(r) => { let mut o = ok(r); o["ack"] = json!(true); o }
        Err(x) => {
            let transient = matches!(x.code, Code::Storage | Code::Internal | Code::NeedRoster | Code::TeamLocked);
            let mut o = fail(&x);
            if !transient {
                let now = a.opt_u("now").unwrap_or(0);
                let logged = (|| -> Res<()> {
                    let gid = a.h("groupId")?;
                    let msg = a.bytes("message")?;
                    s.transact(|st, _, db| {
                        let team = st.group(&gid).map(|g| g.team_id).ok();
                        db.seclog_add(team.as_ref(), now, &json!({"event": "delivery-rejected", "groupId": hex(&gid), "code": x.code.as_str(), "error": x.msg}))?;
                        db.msg_put(&util::sha256(&[&msg]), &team.unwrap_or([0; 32]), now)
                    })
                })();
                o["ack"] = json!(logged.is_ok());
            } else { o["ack"] = json!(false); }
            o
        }
    }
}

fn stateful(st: &mut State, p: &Provider, db: &Db, op: &str, a: &Args) -> Res<J> {
    Ok(match op {
        "device.init" => mls::device_init(st, p, a.id("deviceId")?, a.id("memberId")?)?,
        "device.info" => mls::device_info(st)?,
        "device.certificate" => {
            let d = st.device()?;
            let selfc = a.flag("self");
            let c = DeviceCert {
                server_id: a.id("serverId")?,
                member_id: if selfc { d.member_id } else { a.id("memberId")? },
                device_id: if selfc { d.device_id } else { a.id("deviceId")? },
                identity_key: if selfc { d.identity_public() } else { util::h32(&a.bytes("identityKey")?)? },
                mls_signature_key: if selfc { util::h32(&d.mls_public)? } else { util::h32(&a.bytes("mlsSignatureKey")?)? },
                issuer_device_id: d.device_id,
                pairing_nonce: a.opt_bytes("pairingNonce")?.unwrap_or_default(),
                issued_at: a.u("issuedAt")?,
                sig: [0; 64],
            }.sign(&d.identity_sk);
            json!({"cert": hex(&c.to_bytes()), "hash": hex(&c.hash())})
        }
        "hello.sign" => {
            // §2.2: hello signature input defined by `formats::hello_message`, signed by this device.
            let d = st.device()?;
            let nonce: [u8; 32] = util::h32(&a.bytes("nonce")?)?;
            let m = formats::hello_message(&a.id("serverId")?, &nonce, &d.device_id, a.u("time")?);
            json!({"signature": hex(&util::ed25519_sign(&d.identity_sk, &m))})
        }
        "envelope.sign" => {
            // §6.1: only an envelope that names this device as its signer is signed.
            let d = st.device()?;
            let body = a.bytes("body")?;
            let m = formats::envelope_message(&body)?;
            let mut f = cbor::Fields::new(cbor::decode(&body)?, "envelope")?;
            let signer: Id16 = f.req("deviceId")?.fixed()?;
            ensure!(signer == d.device_id, Code::InvalidInput, "envelope deviceId is not this device");
            json!({"signature": hex(&util::ed25519_sign(&d.identity_sk, &m))})
        }
        "device.adopt" => {
            // §2.3: a freshly initialised device takes the memberId of the certificate its pairing
            // partner issued for exactly its keys (before it belongs to any team or group).
            ensure!(st.teams.is_empty() && st.groups.is_empty(), Code::InvalidInput, "device already belongs to teams; adopt only right after pairing");
            let c = DeviceCert::parse(&a.bytes("cert")?)?;
            let d = st.device()?;
            ensure!(c.device_id == d.device_id && c.identity_key == d.identity_public() && c.mls_signature_key.as_slice() == d.mls_public.as_slice(),
                Code::InvalidInput, "certificate is for another device or other keys");
            ensure!(!c.self_root(), Code::InvalidInput, "a paired device's certificate is issued by another device");
            if let Some(ik) = a.opt_bytes("issuerIdentityKey")? {
                ensure!(c.verify(&ik), Code::BadSignature, "certificate is not signed by the pairing partner");
            }
            st.device.as_mut().expect("checked").member_id = c.member_id;
            mls::device_info(st)?
        }
        "invite.create" => invite_create(st, a)?,
        "invite.verify_join" => invite_verify_join(st, a)?,
        "roster.safety_number" => {
            let t = st.team(&a.h("teamId")?)?;
            let (digits, root) = t.chain.safety_number(&a.id("memberId")?)?;
            json!({"safetyNumber": digits, "rootFingerprint": hex(&util::sha256(&[&root]))})
        }
        "roster.equivocation_diff" => {
            // Owner's decision screen (§7 Ombra): what switching to the conflicting branch undoes, and the reverse.
            let t = st.team(&a.h("teamId")?)?;
            let (version, known_head, conflicting) = match a.raw("evidence") {
                None => {
                    let q = t.chain.equivocation.as_ref().ok_or_else(|| e(Code::NotFound, "no recorded equivocation"))?;
                    (q.version, t.chain.head().clone(), RosterUpdate::parse(&q.conflicting)?.projection)
                }
                Some(_) => {
                    // The known branch continues in this device's chain when it holds that very version.
                    let ev = a.sub("evidence")?;
                    let v = ev.u("version")?;
                    let known = RosterUpdate::parse(&ev.bytes("known")?)?.projection;
                    let head = if t.chain.hash_of(v) == Some(known.hash()) { t.chain.head().clone() } else { known };
                    (v, head, RosterUpdate::parse(&ev.bytes("conflicting")?)?.projection)
                }
            };
            let parent = t.chain.version(version.saturating_sub(1)).ok_or_else(|| e(Code::NeedRoster, "parent version not in the local chain"))?;
            json!({"version": version,
                "undoneIfSwitch": roster::undone(parent, &known_head, &conflicting)?,
                "undoneIfKeep": roster::undone(parent, &conflicting, &known_head)?})
        }
        "team.forget" => {
            // D-5 local wipe: MLS group states, group keys and metadata, the replica, invites and the verified chain of
            // the team leave the encrypted state (the next save rewrites the file without them).
            let tid = a.h("teamId")?;
            st.team(&tid)?;
            let gids: Vec<H32> = st.groups.values().filter(|g| g.team_id == tid).map(|g| g.group_id).collect();
            for gid in &gids {
                if let Ok(mut g) = mls::load_group(p, gid) {
                    g.delete(openmls_traits::OpenMlsProvider::storage(p)).map_err(|x| e(Code::Mls, format!("{x:?}")))?;
                }
                st.groups.remove(&hex(gid));
            }
            st.teams.remove(&hex(&tid));
            db.wipe_team(&tid)?;
            json!({"teamId": hex(&tid), "groups": gids.len(), "remainingTeams": st.teams.len()})
        }
        "key_package" => json!({"keyPackage": hex(&mls::key_package(st, p, a.opt_u("lifetime").unwrap_or(7 * 24 * 3600), a.flag("lastResort"))?)}),
        "team.create" => team_create(st, db, a)?,
        "team.import" => team_import(st, db, a)?,
        "team.status" => {
            let t = st.team(&a.h("teamId")?)?;
            let head = t.chain.head();
            json!({"teamId": hex(&t.chain.team_id), "version": head.version, "hash": hex(&t.chain.hash_of(head.version).unwrap()),
                "frozen": t.chain.frozen(), "joined": t.joined, "removed": t.removed, "pendingJoin": t.join.is_some(),
                "confirmation": mls::confirmation(t, a.u("now")?, a.opt_u("window").unwrap_or(72 * 3600)),
                "equivocation": t.chain.equivocation.as_ref().map(|q| json!({"version": q.version, "known": hex(&q.known), "conflicting": hex(&q.conflicting)})),
                "frozenPeers": t.frozen_peers.iter().collect::<Vec<_>>(), "pendingRecovery": head.pending.as_ref().map(|x| serde_json::to_value(x).unwrap())})
        }
        "roster.apply" => {
            let tid = a.h("teamId")?;
            let t = st.team_mut(&tid)?;
            let mut out = match t.chain.append(&a.bytes("update")?, a.u("now")?)? {
                roster::Appended::New { version, hash } => json!({"version": version, "hash": hex(&hash), "known": false}),
                roster::Appended::Known { version } => json!({"version": version, "known": true}),
            };
            // R2-05: no longer owner/admin → the admin group and the invite secrets go.
            out["adminDropped"] = json!(crate::adminkeys::after_roster(st, p, &tid)?);
            out
        }
        "roster.resolution_create" => {
            // The owner's decision on an equivocation: "known" or "conflicting" branch (R2-08). Either the one
            // recorded in this device's chain, or `evidence` {version, known, conflicting} (RosterUpdates, e.g.
            // from GET /v1/teams/{id}/roster/equivocation) when this device's own chain never froze.
            let d = st.device()?;
            let tid = a.h("teamId")?;
            let t = st.team(&tid)?;
            let r = match a.raw("evidence") {
                None => t.chain.sign_resolution(d.device_id, &d.identity_sk, a.str("chosen")?)?,
                Some(_) => {
                    let ev = a.sub("evidence")?;
                    let known = RosterUpdate::parse(&ev.bytes("known")?)?.projection;
                    let version = ev.u("version")?;
                    ensure!(known.version == version, Code::InvalidInput, "evidence: known update is v{}, not v{version}", known.version);
                    roster::Resolution::sign(tid, version, known.hash(), &ev.bytes("conflicting")?, a.str("chosen")?, d.device_id, &d.identity_sk)?
                }
            };
            json!({"resolution": hex(&r.to_bytes()), "version": r.version, "chosen": hex(&r.chosen), "rejected": hex(&r.rejected)})
        }
        "roster.resolve" => {
            let tid = a.h("teamId")?;
            let t = st.team_mut(&tid)?;
            let r = t.chain.resolve(&a.bytes("resolution")?, a.u("now")?)?;
            let hash = hex(&t.chain.hash_of(r.head).unwrap());
            // Groups whose epoch references a replaced version cannot continue: they are closed and the
            // group is recreated as a new generation on the canonical chain (§1 карантин п.3).
            let mut closed = vec![];
            if r.switched {
                for g in st.groups.values_mut().filter(|g| g.team_id == tid && !g.closed && g.roster_version >= r.version) {
                    g.closed = true;
                    closed.push(hex(&g.group_id));
                }
            }
            json!({"switched": r.switched, "version": r.head, "hash": hash, "closedGroups": closed})
        }
        "roster.check_private" => {
            // R2-07: names arrive in MLS; they are "confirmed" only if they match privateCommit.
            let t = st.team(&a.h("teamId")?)?;
            let v = a.opt_u("version").unwrap_or(t.chain.head_version());
            let pj = t.chain.version(v).ok_or_else(|| e(Code::NotFound, "no such version"))?;
            json!({"confirmed": util::ct_eq(&private_commit(a)?, &pj.private_commit)})
        }
        "roster.head" => {
            let t = st.team(&a.h("teamId")?)?;
            let v = a.opt_u("version").unwrap_or(t.chain.head_version());
            let pj = t.chain.version(v).ok_or_else(|| e(Code::NotFound, "no such version"))?;
            json!({"version": v, "hash": hex(&t.chain.hash_of(v).unwrap()), "projection": proj_json(pj)})
        }
        "roster.updates" => {
            let t = st.team(&a.h("teamId")?)?;
            let from = a.opt_u("from").unwrap_or(1).max(1);
            json!({"updates": t.chain.versions.iter().skip((from - 1) as usize).map(|v| hex(&v.update)).collect::<Vec<_>>()})
        }
        "roster.update" => roster_update(st, a)?,
        "roster.sign" => {
            let mut u = RosterUpdate { projection: a.typed("projection")?, signatures: vec![],
                certs: a.hex_list("certs")?.iter().map(|c| DeviceCert::parse(c)).collect::<Res<_>>()?,
                ownership: a.opt_typed("ownership")?, recovery_key_change_sig: a.opt_bytes("recoveryKeyChangeSig")?.map(|s| s.try_into().map_err(|_| e(Code::InvalidInput, "sig"))).transpose()?,
                evidence: a.hex_list("evidence")? };
            sign_update(st, &mut u)?;
            json!({"update": hex(&u.to_bytes()), "hash": hex(&u.projection.hash())})
        }
        "roster.cosign" => {
            let mut u = RosterUpdate::parse(&a.bytes("update")?)?;
            sign_update(st, &mut u)?;
            json!({"update": hex(&u.to_bytes())})
        }
        "ownership.offer" => {
            let tid = a.h("teamId")?;
            let t = st.team(&tid)?;
            let head = t.chain.head();
            let d = st.device()?;
            let offer = roster::OwnershipOffer { team_id: tid, from: d.member_id, to: a.id("to")?, roster_version: head.version + 1,
                prev_hash: t.chain.hash_of(head.version).unwrap(), new_role_of_from: Role::parse(a.opt_str("newRoleOfFrom").unwrap_or("admin"))?,
                expires_at: a.u("expiresAt")? };
            let sig = Sig { device_id: d.device_id, sig: util::ed25519_sign(&d.identity_sk, &offer.offer_bytes()) };
            json!({"offer": serde_json::to_value(&offer).unwrap(), "offerSig": serde_json::to_value(&sig).unwrap()})
        }
        "ownership.accept" => {
            let offer: roster::OwnershipOffer = a.typed("offer")?;
            let d = st.device()?;
            let sig = Sig { device_id: d.device_id, sig: util::ed25519_sign(&d.identity_sk, &offer.accept_bytes()) };
            json!({"acceptSig": serde_json::to_value(&sig).unwrap()})
        }
        "group.create" => mls::create_group(st, p, a.typed("descriptor")?)?,
        "group.add" => {
            let kps = a.hex_list("keyPackages")?;
            mls::add(st, p, &a.h("groupId")?, &kps, a.u("now")?, a.flag("allowLastResort"))?
        }
        "group.remove" => {
            let ids = a.hex_list("deviceIds")?.iter().map(|x| util::id16(x)).collect::<Res<Vec<_>>>()?;
            mls::remove(st, p, &a.h("groupId")?, &ids)?
        }
        "group.rotate" => mls::rotate(st, p, &a.h("groupId")?)?,
        "group.discard" => mls::discard(st, p, &a.h("groupId")?)?,
        "group.update" => mls::commit(st, p, &a.h("groupId")?, CommitSpec { adds: vec![], removes: vec![], weak: vec![] }, Code::CommitRejected)?,
        "group.join" => mls::join(st, p, &a.bytes("welcome")?)?,
        "group.process" => mls::process(st, p, db, &a.h("groupId")?, &a.bytes("message")?, a.u("now")?)?,
        "group.info" => mls::group_info(st, p, &a.h("groupId")?)?,
        "group.tree_state" => json!({"state": mls::tree_state(st, p, &a.h("groupId")?)?}),
        "group.public_info" => {
            // Epoch GroupInfo + ratchet tree for the server's public verification (public.rs). Uploaded to
            // the own server only; never published (contains external_pub; external commits are rejected).
            use openmls::prelude::*;
            let g = mls::load_group(p, &a.h("groupId")?)?;
            let gi = g.export_group_info(p.crypto(), &mls::signer(st, p)?, true).map_err(|x| e(Code::Mls, format!("{x:?}")))?;
            json!({"groupInfo": hex(&gi.to_bytes().map_err(|x| e(Code::Mls, format!("{x:?}")))?)})
        }
        "group.export" => json!({"secret": hex(&mls::export_secret(p, &a.h("groupId")?, a.str("label")?, &a.opt_bytes("context")?.unwrap_or_default(), a.opt_u("length").unwrap_or(32) as usize)?)}),
        "group.list" => json!({"groups": st.groups.values().map(|g| json!({"groupId": hex(&g.group_id), "teamId": hex(&g.team_id), "type": g.kind.as_str(),
            "closed": g.closed, "evicted": g.evicted, "descriptor": serde_json::to_value(Descriptor::parse(&g.descriptor).ok()).unwrap()})).collect::<Vec<_>>()}),
        "op.send" | "outbox.enqueue" => {
            let gid = a.h("groupId")?;
            let spec = OpSpec {
                op: OpType::parse(a.str("opType")?)?, author: a.opt_id("author")?, item_id: a.opt_id("itemId")?.unwrap_or_else(util::random::<16>),
                rev: match a.opt_u("rev") { Some(r) => r, None => default_rev(st, db, a)? }, recipient: a.opt_id("recipient")?,
                payload: replica::json_cbor(a.raw("payload").unwrap_or(&json!({})))?, op_id: a.opt_id("opId")?.unwrap_or_else(util::random::<16>),
                seq: a.opt_u("seq"),
            };
            let mut r = outbox::enqueue(st, p, db, &gid, spec, a.u("now")?)?;
            // `op.send` = enqueue + seal: the ciphertext for immediate delivery (the row stays until `outbox.sent`).
            if op == "op.send" { r["message"] = outbox::seal(st, p, db, &util::id16(&util::unhex(r["opId"].as_str().unwrap())?)?)?["message"].clone(); }
            r
        }
        "outbox.seal" => outbox::seal(st, p, db, &a.id("opId")?)?,
        "outbox.envelope" => {
            let env = match a.opt_bytes("envelope")? { Some(e) => Some((e, a.bytes("signature")?)), None => None };
            outbox::set_envelope(db, &a.id("opId")?, env)?
        }
        "outbox.conflict" => outbox::conflict(db, &a.id("opId")?)?,
        "outbox.sent" => outbox::sent(db, &a.id("opId")?)?,
        "outbox.drop" => outbox::drop_op(db, &a.id("opId")?, a.opt_str("reason").unwrap_or(""), a.u("now")?)?,
        "outbox.list" => outbox::list(db, a.raw("teamId").map(|_| a.h("teamId")).transpose()?.as_ref())?,
        "sync.cursor" => json!({"cursor": db.meta_get("sync_cursor")?.map(|c| String::from_utf8_lossy(&c).into_owned())}),
        "sync.set_cursor" => {
            db.meta_put("sync_cursor", a.str("cursor")?.as_bytes())?;
            json!({"pruned": db.seen_prune(a.u("now")?)?})
        }
        "seclog.list" => json!({"entries": db.seclog_list(a.raw("teamId").map(|_| a.h("teamId")).transpose()?.as_ref(), a.opt_u("limit").unwrap_or(100))?}),
        "item.list" => {
            let tid = a.h("teamId")?;
            st.team(&tid)?;
            let r = replica::Replica::new(db, tid);
            let mut out = vec![];
            for it in r.items(a.opt_u("limit").unwrap_or(200), a.opt_u("before"))? { out.push(replica::item_view(&it, &r.statuses(&it.key())?)?); }
            json!({"items": out})
        }
        "item.send_addressed" => send_addressed(st, p, db, a)?,
        "item.get" => {
            let tid = a.h("teamId")?;
            st.team(&tid)?;
            let k = itemop::ItemKey { author: a.id("author")?, item_id: a.id("itemId")? };
            replica::Replica::new(db, tid).item_json(&k)?.unwrap_or(J::Null)
        }
        "heartbeat.create" => json!({"heartbeat": hex(&mls::heartbeat_create(st, p, &a.h("teamId")?, a.u("time")?)?)}),
        "heartbeat.accept" => mls::heartbeat_accept(st, p, &a.bytes("heartbeat")?)?,
        "progress.seal" => crate::progress::seal(st, p, a)?,
        "progress.open" => crate::progress::open(st, p, a)?,
        "removal.create" => {
            let tid = a.h("teamId")?;
            let d = st.device()?;
            let t = st.team(&tid)?;
            let n = RemovalNotice { team_id: tid, member_id: a.id("memberId")?, roster_version: t.chain.head_version(), time: a.u("time")?, signer_device_id: d.device_id };
            let mut w = n.signed_bytes();
            w.extend_from_slice(&util::ed25519_sign(&d.identity_sk, &w.clone()));
            json!({"notice": hex(&w)})
        }
        "removal.verify" => removal_verify(st, &a.bytes("notice")?)?,
        "pair.freeze" | "pair.unfreeze" => {
            let m = hex(&a.id("memberId")?);
            let t = st.team_mut(&a.h("teamId")?)?;
            if op == "pair.freeze" { t.frozen_peers.insert(m); } else { t.frozen_peers.remove(&m); }
            json!({"frozenPeers": t.frozen_peers.iter().collect::<Vec<_>>()})
        }
        "share.build" => share_build(st, db, a)?,
        "share.accept" => share_accept(st, db, a)?,
        "names.get" => crate::names::get(st, &a.h("teamId")?)?,
        "names.set" => {
            let devs = a.raw("devices").and_then(|d| d.as_object())
                .map(|m| m.iter().map(|(k, v)| (k.clone(), v.as_str().map(str::to_string))).collect()).unwrap_or_default();
            crate::names::set(st, &a.h("teamId")?, a.opt_str("name"), &devs)?
        }
        "names.seal" => crate::names::seal(st, p, &a.h("groupId")?)?,
        "adminkeys.seal" => crate::adminkeys::seal(st, p, &a.h("groupId")?, a.u("now")?)?,
        #[cfg(feature = "test-attacks")]
        x if x.starts_with("attack.") => crate::attack::call(st, p, db, x, a)?,
        _ => return err(Code::InvalidInput, format!("unknown op '{op}'")),
    })
}

/// Default `rev`: create 1; edit baseRev + 1; delete/collection current item rev + 1; others 0.
fn default_rev(st: &State, db: &Db, a: &Args) -> Res<u64> {
    Ok(match OpType::parse(a.str("opType")?)? {
        OpType::Create => 1,
        OpType::Edit => a.sub("payload")?.u("baseRev")? + 1,
        OpType::Delete | OpType::CollectionAdd | OpType::CollectionRemove => {
            let gid = a.h("groupId")?;
            let tid = st.group(&gid)?.team_id;
            let author = a.opt_id("author")?.unwrap_or(st.device()?.member_id);
            let k = itemop::ItemKey { author, item_id: a.id("itemId")? };
            replica::Replica::new(db, tid).item(&k)?.map(|i| i.rev + 1).unwrap_or(1)
        }
        _ => 0,
    })
}

fn sign_update(st: &State, u: &mut RosterUpdate) -> Res<()> {
    let d = st.device()?;
    ensure!(u.signatures.iter().all(|s| s.device_id != d.device_id), Code::InvalidInput, "already signed by this device");
    u.signatures.push(Sig { device_id: d.device_id, sig: util::ed25519_sign(&d.identity_sk, &u.projection.signed_bytes()) });
    Ok(())
}

fn entry_from_cert(c: &DeviceCert, version: u64) -> DeviceEntry {
    DeviceEntry { device_id: c.device_id, identity_key_hash: c.identity_key_hash(), mls_key_hash: c.mls_key_hash(), cert_hash: c.hash(), added_in_version: version, revoked: None }
}

/// The team's row key is generated here and returned once as `teamKey`: the caller keeps it in the OS key store
/// and supplies it with `team.unlock` after reopening state.db (D-5: destroying it makes old copies unreadable).
fn team_create(st: &mut State, db: &Db, a: &Args) -> Res<J> {
    let d = st.device()?;
    let server_id = a.id("serverId")?;
    let cert = DeviceCert { server_id, member_id: d.member_id, device_id: d.device_id, identity_key: d.identity_public(),
        mls_signature_key: util::h32(&d.mls_public)?, issuer_device_id: d.device_id, pairing_nonce: a.opt_bytes("pairingNonce")?.unwrap_or_default(),
        issued_at: a.u("issuedAt")?, sig: [0; 64] }.sign(&d.identity_sk);
    let proj = Projection {
        team_id: None, version: 1, prev_hash: None, owner: d.member_id,
        recovery: Recovery { owner_recovery_key: util::h32(&a.bytes("recoveryKey")?)?, time_lock: a.opt_u("timeLock").unwrap_or(72 * 3600), custodian: None },
        heartbeat_quorum: a.opt_u("heartbeatQuorum").unwrap_or(2),
        pins: a.hex_list("pins")?.iter().map(|x| util::h32(x)).collect::<Res<_>>()?,
        members: vec![MemberEntry { member_id: d.member_id, role: Role::Owner, devices: vec![entry_from_cert(&cert, 1)] }],
        access: vec![], pending: None, cause: None,
        private_commit: a.opt_bytes("privateCommit")?.map(|x| util::h32(&x)).transpose()?.unwrap_or([0; 32]),
    };
    let mut u = RosterUpdate { projection: proj, signatures: vec![], certs: vec![cert], ownership: None, recovery_key_change_sig: None, evidence: vec![] };
    sign_update(st, &mut u)?;
    let bytes = u.to_bytes();
    let team_id = u.projection.hash();
    let chain = Chain::genesis(server_id, &bytes, Some(team_id))?;
    ensure!(!st.teams.contains_key(&hex(&team_id)), Code::InvalidInput, "team exists");
    st.teams.insert(hex(&team_id), TeamState { chain, join: None, floor_version: 1, joined: true, heartbeats: vec![], frozen_peers: Default::default(),
        removed: false, max_seen_version: 1, invites: Default::default(), names: None });
    let key = zeroize::Zeroizing::new(util::random::<32>());
    db.add_team_key(&team_id, *key)?;
    Ok(json!({"teamId": hex(&team_id), "update": hex(&bytes), "teamKey": hex(key.as_slice())}))
}

/// Import a chain received from the server: genesis must hash to the out-of-band teamId `t`;
/// with `rosterHead` (invite `h`) the chain must contain exactly that version/hash (R2-01) — a
/// shorter or diverging prefix is refused — and a pending join is recorded. With `signedInvite`
/// (§3.4 п.2) the Invite must be signed by the key with fingerprint `a`, name this team and
/// `rosterHead`, be unexpired, and its signer must be an active owner/admin device of `rosterHead`
/// whose certificate carries that key.
fn team_import(st: &mut State, db: &Db, a: &Args) -> Res<J> {
    let team_id = a.h("teamId")?;
    let updates = a.hex_list("updates")?;
    ensure!(!updates.is_empty(), Code::InvalidInput, "no updates");
    let now = a.u("now")?;
    let mut chain = Chain::genesis(a.id("serverId")?, &updates[0], Some(team_id))?;
    for u in &updates[1..] { chain.append(u, now)?; }
    let mut floor = 1;
    let mut join = None;
    if let Some(h) = a.raw("rosterHead") {
        let h = Args::new(h.clone())?;
        let (hv, hh) = (h.u("version")?, h.h("hash")?);
        ensure!(chain.hash_of(hv) == Some(hh), Code::RosterRejected, "chain does not contain the invite's rosterHead v{hv} (stale or forged prefix)");
        let fp = a.h("inviterFingerprint")?;
        if let Some(si) = a.opt_bytes("signedInvite")? {
            let inv = Invite::parse(&si)?;
            ensure!(inv.fingerprint() == fp, Code::RosterRejected, "invite is not signed by the key of the link's fingerprint");
            ensure!(inv.verify(), Code::BadSignature, "invite signature invalid");
            ensure!(inv.team_id == team_id && inv.head_version == hv && inv.head_hash == hh, Code::RosterRejected, "invite names another team or rosterHead than the link");
            ensure!(inv.expires_at > now, Code::RosterRejected, "invite expired");
            let pv = chain.version(hv).expect("rosterHead checked above");
            let ok = pv.active_device(&inv.inviter_device_id).is_some_and(|(m, _)| m.role.is_admin())
                && chain.cert(&inv.inviter_device_id).is_some_and(|c| c.identity_key == inv.inviter_identity_key);
            ensure!(ok, Code::RosterRejected, "invite signer is not an active owner/admin device of rosterHead");
        }
        floor = hv;
        join = Some(PendingJoin { inviter_fingerprint: fp, head_version: hv, head_hash: hh });
    }
    let key = hex(&team_id);
    if let Some(t) = st.teams.get_mut(&key) {
        ensure!(!t.joined, Code::InvalidInput, "team already joined");
        t.chain = chain;
        t.join = join;
        t.floor_version = floor;
    } else {
        st.teams.insert(key, TeamState { chain, join, floor_version: floor, joined: false, heartbeats: vec![], frozen_peers: Default::default(),
            removed: false, max_seen_version: 0, invites: Default::default(), names: None });
    }
    let mut out = json!({"teamId": hex(&team_id), "version": st.team(&team_id)?.chain.head_version()});
    if !db.is_unlocked(&team_id) {
        let key = zeroize::Zeroizing::new(util::random::<32>());
        db.add_team_key(&team_id, *key)?;
        out["teamKey"] = json!(hex(key.as_slice()));
    }
    Ok(out)
}

/// Build the next projection from high-level edits, sign it with this device, optionally apply.
fn roster_update(st: &mut State, a: &Args) -> Res<J> {
    let tid = a.h("teamId")?;
    let t = st.team(&tid)?;
    let head = t.chain.head().clone();
    let mut n = head.clone();
    let v = head.version + 1;
    n.version = v;
    n.team_id = Some(tid);
    n.prev_hash = Some(t.chain.hash_of(head.version).unwrap());
    n.cause = a.opt_typed::<Cause>("cause")?;
    let mut certs = vec![];
    for m in a.list("addMembers") {
        let m = Args::new(m)?;
        let mcerts = m.hex_list("certs")?.iter().map(|c| DeviceCert::parse(c)).collect::<Res<Vec<_>>>()?;
        let devices = mcerts.iter().map(|c| entry_from_cert(c, v)).collect();
        n.members.push(MemberEntry { member_id: m.id("memberId")?, role: Role::parse(m.opt_str("role").unwrap_or("member"))?, devices });
        certs.extend(mcerts);
    }
    for c in a.hex_list("addDevices")? {
        let c = DeviceCert::parse(&c)?;
        let m = n.members.iter_mut().find(|m| m.member_id == c.member_id).ok_or_else(|| e(Code::InvalidInput, "addDevices: unknown member"))?;
        m.devices.push(entry_from_cert(&c, v));
        certs.push(c);
    }
    for r in a.list("revokeDevices") {
        let r = Args::new(r)?;
        let id = r.id("deviceId")?;
        let since = r.opt_u("compromisedSince").unwrap_or(v);
        let d = n.members.iter_mut().flat_map(|m| m.devices.iter_mut()).find(|d| d.device_id == id).ok_or_else(|| e(Code::InvalidInput, "revoke: unknown device"))?;
        d.revoked = Some(roster::Revocation { version: v, compromised_since: since });
    }
    // Cascade (§2.3 п.5, R2-10): a device issued by a device revoked here at or after its
    // compromisedSince is revoked too (as compromised from the version it was added), to a fixpoint.
    let mut cascaded: Vec<String> = vec![];
    loop {
        let revoked_now: Vec<(Id16, u64)> = n.members.iter().flat_map(|m| m.devices.iter())
            .filter_map(|d| d.revoked.as_ref().filter(|r| r.version == v).map(|r| (d.device_id, r.compromised_since))).collect();
        let issuer_of = |id: &Id16| t.chain.cert(id).or_else(|| certs.iter().find(|c| c.device_id == *id)).map(|c| c.issuer_device_id);
        let mut changed = false;
        for d in n.members.iter_mut().flat_map(|m| m.devices.iter_mut()).filter(|d| d.revoked.is_none()) {
            let Some(issuer) = issuer_of(&d.device_id) else { continue };
            if issuer != d.device_id && revoked_now.iter().any(|(r, since)| *r == issuer && d.added_in_version >= *since) {
                d.revoked = Some(roster::Revocation { version: v, compromised_since: d.added_in_version });
                cascaded.push(hex(&d.device_id));
                changed = true;
            }
        }
        if !changed { break; }
    }
    for m in a.hex_list("removeMembers")? {
        let m = util::id16(&m)?;
        n.members.retain(|x| x.member_id != m);
        n.access.retain(|x| x.member_id != m);
    }
    for r in a.list("setRoles") {
        let r = Args::new(r)?;
        let id = r.id("memberId")?;
        let m = n.members.iter_mut().find(|m| m.member_id == id).ok_or_else(|| e(Code::InvalidInput, "setRoles: unknown member"))?;
        m.role = Role::parse(r.str("role")?)?;
    }
    for g in a.list("grantAccess") {
        let g = Args::new(g)?;
        let level = match g.opt_str("level").unwrap_or("contributor") { "viewer" => Level::Viewer, _ => Level::Contributor };
        let (pid, mid) = (g.id("projectId")?, g.id("memberId")?);
        n.access.retain(|x| !(x.project_id == pid && x.member_id == mid));
        n.access.push(AccessEntry { project_id: pid, member_id: mid, level });
    }
    for g in a.list("revokeAccess") {
        let g = Args::new(g)?;
        let (pid, mid) = (g.id("projectId")?, g.id("memberId")?);
        n.access.retain(|x| !(x.project_id == pid && x.member_id == mid));
    }
    if a.raw("pins").is_some() { n.pins = a.hex_list("pins")?.iter().map(|x| util::h32(x)).collect::<Res<_>>()?; }
    if let Some(q) = a.opt_u("heartbeatQuorum") { n.heartbeat_quorum = q; }
    if let Some(pc) = a.opt_bytes("privateCommit")? { n.private_commit = util::h32(&pc)?; }
    if let Some(r) = a.opt_typed::<Recovery>("setRecovery")? { n.recovery = r; }
    if let Some(pd) = a.opt_typed::<Pending>("setPending")? { n.pending = Some(pd); }
    if a.flag("clearPending") { n.pending = None; }
    if a.flag("applyRecovery") {
        let pd = head.pending.clone().ok_or_else(|| e(Code::InvalidInput, "no pending recovery"))?;
        let old = n.owner;
        n.owner = pd.proposal.new_owner;
        for m in n.members.iter_mut() {
            if m.member_id == old { m.role = Role::Member; }
            if m.member_id == pd.proposal.new_owner { m.role = Role::Owner; }
        }
        n.pending = None;
    }
    let ownership: Option<Ownership> = a.opt_typed("ownership")?;
    if let Some(o) = &ownership {
        n.owner = o.offer.to;
        for m in n.members.iter_mut() {
            if m.member_id == o.offer.from { m.role = o.offer.new_role_of_from; }
            if m.member_id == o.offer.to { m.role = Role::Owner; }
        }
    }
    if let Some(ow) = a.opt_id("setOwner")? { n.owner = ow; }
    n.members.sort_by(|x, y| x.member_id.cmp(&y.member_id));
    for m in n.members.iter_mut() { m.devices.sort_by(|x, y| x.device_id.cmp(&y.device_id)); }
    n.access.sort_by(|x, y| (x.project_id, x.member_id).cmp(&(y.project_id, y.member_id)));
    let mut u = RosterUpdate { projection: n, signatures: vec![], certs, ownership,
        recovery_key_change_sig: a.opt_bytes("recoveryKeyChangeSig")?.map(|s| s.try_into().map_err(|_| e(Code::InvalidInput, "sig"))).transpose()?,
        evidence: a.hex_list("evidence")? };
    if a.raw("sign").and_then(|x| x.as_bool()).unwrap_or(true) { sign_update(st, &mut u)?; }
    let bytes = u.to_bytes();
    let mut out = json!({"update": hex(&bytes), "version": v, "hash": hex(&u.projection.hash()), "projection": proj_json(&u.projection), "cascaded": cascaded});
    if a.flag("apply") {
        st.team_mut(&tid)?.chain.append(&bytes, a.u("now")?)?;
        out["applied"] = json!(true);
    }
    Ok(out)
}

/// §3.4 п.1: a new Invite for this team, signed by this (owner/admin) device. `k` goes only into the
/// link fragment; `k_adm` stays in this device's state for the `bind` check; the server gets `verifier`.
fn invite_create(st: &mut State, a: &Args) -> Res<J> {
    let tid = a.h("teamId")?;
    let d = st.device()?.clone();
    let t = st.team(&tid)?;
    ensure!(!t.chain.frozen(), Code::Frozen, "team frozen");
    let head = t.chain.head();
    ensure!(head.active_device(&d.device_id).is_some_and(|(m, _)| m.role.is_admin()), Code::InvalidInput, "only owner/admin devices create invites");
    let role = a.opt_str("role").unwrap_or("member").to_string();
    ensure!(role == "member" || (role == "admin" && head.role(&d.member_id) == Some(Role::Owner)), Code::InvalidInput, "role must be member (admin invites: owner only)");
    let max_uses = a.opt_u("maxUses").unwrap_or(1);
    ensure!((1..=1000).contains(&max_uses), Code::InvalidInput, "maxUses out of range");
    let expires_at = a.u("expiresAt")?;
    let mut invite_id = util::random::<16>();
    invite_id[6] = (invite_id[6] & 0x0f) | 0x40; // RFC 9562 version 4
    invite_id[8] = (invite_id[8] & 0x3f) | 0x80;
    let k = zeroize::Zeroizing::new(util::random::<32>());
    let keys = formats::invite_keys(k.as_slice(), &invite_id)?;
    let inv = Invite { invite_id, team_id: tid, head_version: head.version, head_hash: t.chain.hash_of(head.version).unwrap(), role: role.clone(),
        expires_at, max_uses, verifier: keys.verifier, inviter_device_id: d.device_id, inviter_identity_key: d.identity_public(), sig: [0; 64] }
        .sign(&d.identity_sk);
    st.team_mut(&tid)?.invites.insert(hex(&invite_id), InviteSecret { k_adm: keys.k_adm, role, max_uses, expires_at, inviter_device: Some(d.device_id) });
    Ok(json!({"inviteId": hex(&invite_id), "k": hex(k.as_slice()), "verifier": hex(&keys.verifier), "signedInvite": hex(&inv.to_bytes()),
        "rosterHead": {"version": inv.head_version, "hash": hex(&inv.head_hash)}, "inviterFingerprint": hex(&inv.fingerprint()),
        "role": inv.role, "maxUses": max_uses, "expiresAt": expires_at}))
}

/// §3.4 п.4–5 on the admin device: `bind` against the stored `k_adm`, every device's
/// certificate matching the bound keys (root self-issued, siblings issued by it, this server),
/// and whether the request may be accepted without a manual safety-number check (valid `bind`
/// and a personal invite, `maxUses = 1`).
fn invite_verify_join(st: &State, a: &Args) -> Res<J> {
    let tid = a.h("teamId")?;
    let t = st.team(&tid)?;
    let iid = a.id("inviteId")?;
    let mid = a.id("memberId")?;
    let not = |why: String| Ok(json!({"valid": false, "autoAccept": false, "reason": why}));
    let Some(sec) = t.invites.get(&hex(&iid)) else { return not("no k_adm for this invite on this device (not its creator, not received in the admin group)".into()) };
    // R2-05: an invite of a device that is no longer an active owner/admin is void.
    if let Some(inviter) = sec.inviter_device {
        if !t.chain.head().active_device(&inviter).is_some_and(|(m, _)| m.role.is_admin()) {
            return not("the inviting device is no longer an owner/admin".into());
        }
    }
    let devs = bind_devices(a)?;
    if !formats::verify_bind(&sec.k_adm, &iid, &mid, &devs, &a.bytes("bind")?)? {
        return not("bind does not match: the request was changed on the way".into());
    }
    let certs = a.list("devices").into_iter().map(|d| Args::new(d).and_then(|d| d.bytes("certificate")).and_then(|c| DeviceCert::parse(&c))).collect::<Res<Vec<_>>>()?;
    let root = certs.iter().find(|c| c.self_root());
    for (c, d) in certs.iter().zip(devs.iter()) {
        let issuer_key = root.map(|r| r.identity_key);
        if c.device_id != d.device_id || c.identity_key != d.identity_key || c.mls_signature_key != d.mls_signature_key || c.member_id != mid
            || c.server_id != t.chain.server_id || issuer_key.is_none_or(|k| !c.verify(&k)) || root.is_some_and(|r| c.issuer_device_id != r.device_id) {
            return not(format!("certificate of device {} does not match the bound keys", hex(&d.device_id)));
        }
    }
    if t.chain.seen_members.contains(&hex(&mid)) { return not("memberId already appeared in this team (N2b)".into()); }
    let expired = a.u("now")? >= sec.expires_at;
    if expired { return not("invite expired".into()); }
    Ok(json!({"valid": true, "autoAccept": sec.max_uses == 1, "role": sec.role, "maxUses": sec.max_uses}))
}

fn removal_verify(st: &mut State, wire: &[u8]) -> Res<J> {
    let (n, signed, sig) = RemovalNotice::parse(wire)?;
    let me = st.device()?.member_id;
    let t = st.team(&n.team_id)?;
    let pv = t.chain.version(n.roster_version).ok_or_else(|| e(Code::NeedRoster, "notice references unknown roster version"))?;
    let not = |why: &str| Ok(json!({"accepted": false, "reason": why}));
    let Some((m, _)) = pv.active_device(&n.signer_device_id) else { return not("signer not an active device in that version") };
    if !m.role.is_admin() { return not("signer is not owner/admin"); }
    let Some(c) = t.chain.cert(&n.signer_device_id) else { return not("no certificate") };
    if !util::ed25519_verify(&c.identity_key, &signed, &sig) { return not("bad signature"); }
    if pv.member(&n.member_id).is_some() { return not("member still present in that roster version"); }
    if !t.chain.seen_members.contains(&hex(&n.member_id)) { return not("member never in this team"); }
    let about_me = n.member_id == me;
    if about_me { st.team_mut(&n.team_id)?.removed = true; }
    Ok(json!({"accepted": true, "aboutMe": about_me}))
}

/// Find this member's active pair group with `peer` (highest generation, not closed).
fn pair_group(st: &State, team: &H32, me: &Id16, peer: &Id16) -> Option<H32> {
    st.groups.values().filter(|g| g.team_id == *team && !g.closed && !g.evicted && g.kind == GroupType::Pair)
        .filter_map(|g| Descriptor::parse(&g.descriptor).ok().map(|d| (g.group_id, d)))
        .filter(|(_, d)| d.members.contains(me) && d.members.contains(peer))
        .max_by_key(|(_, d)| d.generation).map(|(g, _)| g)
}

/// R5-10: one independent copy per recipient, each in the author–recipient pair, carrying only
/// that recipient; collection keys filtered to what that recipient may see. Copies are distinct
/// items (own itemId, own opId): the author's replica holds each `(author, itemId)` once, and a
/// recipient's status references only its own copy. An explicit `itemId` needs exactly one recipient.
fn send_addressed(st: &mut State, p: &Provider, db: &Db, a: &Args) -> Res<J> {
    let tid = a.h("teamId")?;
    let me = st.device()?.member_id;
    let recipients = a.hex_list("recipients")?;
    let fixed_id = a.opt_id("itemId")?;
    ensure!(fixed_id.is_none() || recipients.len() == 1, Code::InvalidInput, "explicit itemId only with a single recipient");
    let now = a.u("now")?;
    let kind = itemop::Kind::parse(a.str("kind")?)?;
    let mut out = vec![];
    for r in recipients {
        let r = util::id16(&r)?;
        let item_id = fixed_id.unwrap_or_else(util::random::<16>);
        let gid = pair_group(st, &tid, &me, &r).ok_or_else(|| e(Code::NotFound, format!("no pair group with {}", hex(&r))))?;
        let team = st.team(&tid)?;
        let head = team.chain.head();
        let mut m = MapB::new().put("kind", itemop::kind_value(kind))
            .put("audience", itemop::audience_value(&Audience::Member(r)))
            .put("createdAt", Value::U(a.opt_u("createdAt").unwrap_or(now)));
        for k in ["anchor", "context", "attachments"] {
            if let Some(v) = a.raw(k) { m = m.put(k, replica::json_cbor(v)?); }
        }
        if let Some(b) = a.raw("body") {
            let mut body = replica::json_cbor(b)?;
            if kind == itemop::Kind::Collection {
                // Keep only keys of space items in the recipient's scopes and items addressed to it.
                let mut f = cbor::Fields::new(body, "collection")?;
                let keys = f.req("itemKeys")?.array()?.into_iter().map(itemop::item_key_from).collect::<Res<Vec<_>>>()?;
                let keep: Vec<Value> = keys.into_iter().filter(|k| replica::Replica::new(db, tid).item(k).ok().flatten().is_some_and(|it| match (&it.space, &it.recipient) {
                    (Some(s), _) => s.as_slice() == team.chain.team_id.as_slice() && head.member(&r).is_some()
                        || <Id16>::try_from(s.as_slice()).is_ok_and(|pid| head.access_level(&pid, &r).is_some()),
                    (None, Some(rc)) => *rc == r,
                    _ => false,
                })).map(|k| itemop::item_key_value(&k)).collect();
                let title = itemop::collection_title(&mut f)?;
                body = MapB::new().put("itemKeys", Value::A(keep)).put_opt("title", title).build();
            }
            m = m.put("body", body);
        }
        let spec = OpSpec { op: OpType::Create, author: None, item_id, rev: 1, recipient: Some(r), payload: m.build(), op_id: util::random::<16>(), seq: None };
        let res = outbox::enqueue(st, p, db, &gid, spec, now)?;
        let sealed = outbox::seal(st, p, db, &util::id16(&util::unhex(res["opId"].as_str().unwrap())?)?)?;
        out.push(json!({"recipient": hex(&r), "groupId": hex(&gid), "itemId": hex(&item_id), "message": sealed["message"], "opId": res["opId"]}));
    }
    Ok(json!({"copies": out}))
}

/// R5-07: archive of `space` items in `scopes` the target may access in the current roster, each
/// with all its stored signed ops, tombstones for deleted ones; no statuses, no member items.
fn share_build(st: &State, db: &Db, a: &Args) -> Res<J> {
    let tid = a.h("teamId")?;
    let t = st.team(&tid)?;
    let head = t.chain.head();
    let target = a.id("targetMemberId")?;
    let me = st.device()?.member_id;
    let scopes = a.hex_list("scopes")?;
    let personal = target == me;
    let mut ops = vec![];
    let mut count = 0u64;
    for it in replica::Replica::new(db, tid).items(u64::MAX, None)?.iter() {
        let include = match (&it.space, &it.recipient) {
            (Some(s), _) => scopes.contains(s) && (s.as_slice() == tid.as_slice() || <Id16>::try_from(s.as_slice()).is_ok_and(|pid| head.access_level(&pid, &target).is_some())),
            (None, Some(r)) => personal && (*r == me || it.author == me),
            _ => false,
        };
        if !include { continue; }
        count += 1;
        match &it.tombstone { Some(tb) => ops.push(cbor::b(tb)), None => ops.extend(it.ops.iter().map(|o| cbor::b(o))) }
    }
    let share_id = a.id("shareId")?;
    let archive = cbor::encode(&MapB::new().put("shareId", cbor::b(&share_id)).put("ops", Value::A(ops)).build());
    let sha = util::sha256(&[&archive]);
    Ok(json!({"archive": hex(&archive), "sha256": hex(&sha), "count": count,
        "payload": {"shareId": format!("hex:{}", hex(&share_id)), "targetMemberId": format!("hex:{}", hex(&target)),
            "targetDeviceId": format!("hex:{}", hex(&a.id("targetDeviceId")?)), "scopes": scopes.iter().map(|s| format!("hex:{}", hex(s))).collect::<Vec<_>>(),
            "count": count, "sha256": format!("hex:{}", hex(&sha))}}))
}

/// Receiver side of §3.2 / N3 / N4: requires the matching `share` event targeting this device,
/// its signer still an admin in *our current* roster (team share) or ourselves (personal), then
/// verifies every op at its own rosterVersion; tombstones first; statuses rejected.
fn share_accept(st: &mut State, db: &Db, a: &Args) -> Res<J> {
    let tid = a.h("teamId")?;
    let d = st.device()?.clone();
    let archive = a.bytes("archive")?;
    let mut f = cbor::Fields::new(cbor::decode(&archive)?, "archive")?;
    let share_id: Id16 = f.req("shareId")?.fixed()?;
    let raw_ops: Vec<Vec<u8>> = f.req("ops")?.array()?.iter().map(|o| o.bytes().map(|b| b.to_vec())).collect::<Result<_, _>>()?;
    f.finish()?;
    let t = st.team(&tid)?;
    let head = t.chain.head().clone();
    let rep = replica::Replica::new(db, tid);
    let sh = rep.share(&share_id)?.ok_or_else(|| e(Code::OpRejected, "no signed share event for this archive"))?;
    ensure!(!sh.consumed, Code::OpRejected, "share already consumed");
    ensure!(util::sha256(&[&archive]) == sh.sha256, Code::OpRejected, "archive hash != share event");
    ensure!(sh.target_member == d.member_id && sh.target_device == d.device_id, Code::OpRejected, "share targets another device");
    match sh.group_kind {
        GroupType::Team => ensure!(head.role(&sh.signer_member).is_some_and(Role::is_admin)
            && head.active_device(&sh.signer_device).is_some(), Code::OpRejected, "share signer is no longer an admin in the current roster (N3)"),
        GroupType::Personal => ensure!(sh.signer_member == d.member_id, Code::OpRejected, "personal share not by self"),
        _ => return err(Code::OpRejected, "share from a group type that cannot share"),
    }
    let chain = t.chain.clone();
    let mut ops: Vec<(SignedOp, Vec<u8>)> = vec![];
    let mut rejected = vec![];
    for r in raw_ops {
        match SignedOp::parse(&r) {
            Ok(o) => ops.push((o, r)),
            Err(x) => rejected.push(json!({"reason": x.msg})),
        }
    }
    // Tombstones first, then creates, then rev-ordered ops.
    ops.sort_by_key(|(o, _)| (match o.header.op_type { 3 => 0, 1 => 1, _ => 2 }, o.header.rev));
    let mut accepted = 0;
    let personal = sh.group_kind == GroupType::Personal;
    for (o, raw) in ops {
        let res = (|| -> Res<()> {
            let at = replica::verify_at_version(&chain, &o)?.clone();
            let ty = o.op_type()?;
            let payload = Payload::parse(ty, &o.payload)?;
            let scope_ok = |scope: &[u8]| sh.scopes.iter().any(|s| s.as_slice() == scope)
                && (scope == tid.as_slice() || <Id16>::try_from(scope).is_ok_and(|pid| head.access_level(&pid, &d.member_id).is_some()));
            match (&payload, ty) {
                (Payload::Create { audience: Audience::Space(s), .. }, _) => ensure!(scope_ok(s), Code::OpRejected, "item scope not in share scopes / no access"),
                (Payload::Create { audience: Audience::Member(r), .. }, _) => ensure!(personal && (*r == d.member_id || o.header.author_member_id == d.member_id),
                    Code::OpRejected, "member (addressed) items cannot be shared to another member"),
                (_, OpType::Status | OpType::Share | OpType::SeqHead | OpType::SeqSkip) => return err(Code::OpRejected, "only item ops travel in a share archive"),
                _ => {}
            }
            let key = o.item_key();
            if ty == OpType::Delete && rep.item(&key)?.is_none() {
                // Tombstone-only item: remember it so a later create cannot resurrect it.
                ensure!(o.header.signer_member_id == key.author, Code::OpRejected, "tombstone of an unknown item not signed by its author");
                rep.put_item(&replica::ItemRec {
                    author: key.author, item_id: key.item_id, kind: String::new(), space: None, recipient: None, home: String::new(),
                    rev: o.header.rev, created_at: 0, content: vec![], ops: vec![], tombstone: Some(raw.clone()), pending: vec![],
                    head: None, replaced: None })?;
                return Ok(());
            }
            let cx = ApplyCtx { chain: &chain, at: &at, head: &head, home: None, group_kind: None, desc: None, me: Some(d.device_id) };
            match db.savepoint(|| rep.apply(&cx, &o, &raw))? {
                replica::Outcome::Ignored(w) => err(Code::OpRejected, w),
                _ => Ok(()),
            }
        })();
        match res { Ok(()) => accepted += 1, Err(x) => rejected.push(json!({"opId": hex(&o.op_id), "reason": x.msg})) }
    }
    rep.put_share(&share_id, &replica::ShareRec { consumed: true, ..sh })?;
    Ok(json!({"accepted": accepted, "rejected": rejected}))
}
