//! Generates the cross-language test vectors in `vectors/*.json` (SERVER_DESIGN §7, §9 row S1).
//!
//! Usage: `cargo run --release --bin gen-vectors -- [out-dir]` (default `vectors`).
//! Every key, id and time is derived from fixed labels (`SHA-256("ombra-vector/<label>")`), so the
//! output is byte-identical on every run. Each case is self-checked: the expected bytes are built
//! here independently (fixed layouts by hand, structures from library types) and must agree with
//! `ombra_core::api::call_stateless`, otherwise the generator panics.
//!
//! File format: `{description, generatedBy, cases: [{name, op, input, expect | error}]}`; a runner
//! sends `{"op": op, ...input}` to the stateless ABI; `expect` is a subset of `result`, `error`
//! the exact `code` of an `ok: false` response.

use std::collections::BTreeSet;
use std::path::Path;

use ombra_core::api;
use ombra_core::cbor::{self, b, t, MapB, Value};
use ombra_core::cert::DeviceCert;
use ombra_core::descriptor::{Descriptor, GroupType};
use ombra_core::formats::{self, BindDevice, RemovalNotice};
use ombra_core::heartbeat::{HbGroup, Heartbeat};
use ombra_core::itemop::{Header, OpType, SignedOp, ZERO16};
use ombra_core::replica;
use ombra_core::roster::{AccessEntry, DeviceEntry, Level, MemberEntry, Projection, Recovery, Revocation, Role, RosterUpdate, Sig};
use ombra_core::util::{self, hex, Id16, H32};
use serde_json::{json, Map, Value as J};

const GENERATED_BY: &str = "cargo run --release --bin gen-vectors -- vectors";
const T0: u64 = 1_760_000_000;
const NOW: u64 = 1_790_000_000;

fn main() {
    let dir = std::env::args().nth(1).unwrap_or_else(|| "vectors".into());
    ombra_core_vectors(&dir);
}

fn ombra_core_vectors(dir: &str) {
    let dir = Path::new(dir);
    std::fs::create_dir_all(dir).expect("create output dir");
    let mut names = BTreeSet::new();
    let w = World::build();
    let files = [
        ("canonical.json", "Canonical byte layouts: invite keys and bind (§3.4), heartbeat (§4.1), item-op inner signature (§3.0), RemovalNotice, hello, envelope, group descriptor, roster projection, DeviceCertificate, privateCommit.", canonical(&w)),
        ("cbor.json", "Strict deterministic CBOR decoder (RFC 8949 §4.2.1): `canonical` is true only for bytes that re-encode to themselves.", cbor_cases()),
        ("roster.json", "Roster chain verification (§4.1, N2b, R2-10): whole chains from genesis, verdict for the last update.", roster_cases(&w)),
        ("itemop.json", "Item operations (§3.0): closed payload schemas (`itemop.parse`) and inner signature + signer status at the op's own rosterVersion (`itemop.verify`, N4, R2-10).", itemop_cases(&w)),
    ];
    for (file, description, cases) in files {
        assert!(!cases.is_empty(), "{file}: no cases");
        for c in &cases {
            let n = c["name"].as_str().unwrap().to_string();
            assert!(names.insert(n.clone()), "duplicate case name {n}");
        }
        let doc = json!({"description": description, "generatedBy": GENERATED_BY, "cases": cases});
        let mut s = serde_json::to_string_pretty(&doc).unwrap();
        s.push('\n');
        std::fs::write(dir.join(file), s).expect("write vector file");
        println!("{file}: {} cases", cases.len());
    }
}

// ======================= case helpers =======================

fn call(op: &str, input: &J) -> J {
    let mut m = input.as_object().expect("input is an object").clone();
    assert!(!m.contains_key("op"), "input must not use the reserved key 'op'");
    m.insert("op".into(), json!(op));
    api::call_stateless(J::Object(m))
}

/// Valid case: `expect` (built independently) must be a subset of the actual result.
fn ok(out: &mut Vec<J>, name: &str, op: &str, input: J, expect: J) {
    let r = call(op, &input);
    assert_eq!(r["ok"], json!(true), "{name}: expected ok, got {r}");
    for (k, v) in expect.as_object().unwrap() {
        assert_eq!(&r["result"][k], v, "{name}: result.{k} disagrees with the independently built value");
    }
    out.push(json!({"name": name, "op": op, "input": input, "expect": expect}));
}

fn fail(out: &mut Vec<J>, name: &str, op: &str, input: J, code: &str) {
    let r = call(op, &input);
    assert_eq!(r["ok"], json!(false), "{name}: expected error {code}, got {r}");
    assert_eq!(r["code"], json!(code), "{name}: wrong error code: {r}");
    out.push(json!({"name": name, "op": op, "input": input, "error": code}));
}

fn seed(label: &str) -> [u8; 32] { util::sha256(&[b"ombra-vector/", label.as_bytes()]) }
fn id(label: &str) -> Id16 { seed(&format!("id/{label}"))[..16].try_into().unwrap() }
fn be(n: u64) -> [u8; 8] { n.to_be_bytes() }
fn dom(d: &str) -> Vec<u8> { [d.as_bytes(), &[0u8]].concat() }

/// CBOR value → JSON in the `replica::json_cbor` input convention (bytes as "hex:…").
fn input_json(v: &Value) -> J {
    match v {
        Value::U(n) => json!(n),
        Value::B(x) => json!(format!("hex:{}", hex(x))),
        Value::T(s) => json!(s),
        Value::A(a) => J::Array(a.iter().map(input_json).collect()),
        Value::M(m) => J::Object(m.iter().map(|(k, v)| match k {
            Value::T(k) => (k.clone(), input_json(v)),
            _ => panic!("payload maps have text keys"),
        }).collect::<Map<_, _>>()),
        Value::Bool(x) => json!(x),
        Value::Null => J::Null,
        Value::N(_) => panic!("negative integers are not representable in payload JSON"),
    }
}

// ======================= roster world =======================

#[derive(Clone)]
struct Dev { member: Id16, device: Id16, sk: [u8; 32], pk: [u8; 32], mls: [u8; 32] }

fn dev(member: &str, name: &str) -> Dev {
    let sk = seed(&format!("sk/{name}"));
    Dev { member: id(&format!("member/{member}")), device: id(&format!("device/{name}")), sk, pk: util::ed25519_public(&sk), mls: seed(&format!("mls/{name}")) }
}

fn cert_for(d: &Dev, issuer: &Dev, server: Id16, issued_at: u64) -> DeviceCert {
    DeviceCert { server_id: server, member_id: d.member, device_id: d.device, identity_key: d.pk, mls_signature_key: d.mls,
        issuer_device_id: issuer.device, pairing_nonce: id(&format!("nonce/{}", hex(&d.device))).to_vec(), issued_at, sig: [0; 64] }
        .sign(&issuer.sk)
}

fn entry(c: &DeviceCert, v: u64) -> DeviceEntry {
    DeviceEntry { device_id: c.device_id, identity_key_hash: util::sha256(&[&c.identity_key]), mls_key_hash: util::sha256(&[&c.mls_signature_key]),
        cert_hash: util::sha256(&[&c.to_bytes()]), added_in_version: v, revoked: None }
}

fn add_member(p: &mut Projection, member: Id16, role: Role, mut devices: Vec<DeviceEntry>) {
    devices.sort_by(|a, b| a.device_id.cmp(&b.device_id));
    p.members.push(MemberEntry { member_id: member, role, devices });
    p.members.sort_by(|a, b| a.member_id.cmp(&b.member_id));
}

fn add_device(p: &mut Projection, member: &Id16, e: DeviceEntry) {
    let m = p.members.iter_mut().find(|m| &m.member_id == member).expect("member listed");
    m.devices.push(e);
    m.devices.sort_by(|a, b| a.device_id.cmp(&b.device_id));
}

fn revoke(p: &mut Projection, device: &Id16, compromised_since: u64) {
    let version = p.version;
    let d = p.members.iter_mut().flat_map(|m| m.devices.iter_mut()).find(|d| &d.device_id == device).expect("device listed");
    d.revoked = Some(Revocation { version, compromised_since });
}

fn set_role(p: &mut Projection, member: &Id16, role: Role) {
    p.members.iter_mut().find(|m| &m.member_id == member).expect("member listed").role = role;
}

fn remove_member(p: &mut Projection, member: &Id16) { p.members.retain(|m| &m.member_id != member); }

fn next(p: &Projection, team: &H32) -> Projection {
    let mut n = p.clone();
    n.team_id = Some(*team);
    n.version = p.version + 1;
    n.prev_hash = Some(p.hash());
    n.cause = None;
    n
}

fn update(p: &Projection, certs: Vec<DeviceCert>, signers: &[&Dev]) -> Vec<u8> {
    let sb = p.signed_bytes();
    // Independent check of the signed layout: "ombra/v1/roster" ‖ 0x00 ‖ canonical(projection).
    assert_eq!(sb, [dom("ombra/v1/roster"), p.canonical()].concat());
    let signatures = signers.iter().map(|d| Sig { device_id: d.device, sig: util::ed25519_sign(&d.sk, &sb) }).collect();
    RosterUpdate { projection: p.clone(), signatures, certs, ownership: None, recovery_key_change_sig: None, evidence: vec![] }.to_bytes()
}

struct World {
    server: Id16,
    other_server: Id16,
    team: H32,
    project: Id16,
    o1: Dev, a1: Dev, b1: Dev, m1: Dev, m2: Dev, x1: Dev, m3: Dev,
    /// Main chain v1..v6: genesis, two admins, member M (+ access, pin), M's second device m2,
    /// plain revocation of m2, removal of M.
    p: Vec<Projection>,
    u: Vec<Vec<u8>>,
    certs: Vec<DeviceCert>,
    /// Cascade v5 (after main v4): m1 revoked compromised since v3, its child m2 revoked too.
    cascade_p5: Projection,
    cascade_u5: Vec<u8>,
}

impl World {
    fn build() -> World {
        let server = id("server");
        let other_server = id("server/other");
        let project = id("project/p");
        let (o1, a1, b1) = (dev("owner", "o1"), dev("admin-a", "a1"), dev("admin-b", "b1"));
        let (m1, m2, x1, m3) = (dev("member-m", "m1"), dev("member-m", "m2"), dev("member-x", "x1"), dev("member-m", "m3"));
        let mut p = vec![];
        let mut u = vec![];
        let mut certs = vec![];

        // v1 genesis: owner O with self-issued o1.
        let c_o1 = cert_for(&o1, &o1, server, T0 + 1);
        let mut g = Projection {
            team_id: None, version: 1, prev_hash: None, owner: o1.member,
            recovery: Recovery { owner_recovery_key: util::ed25519_public(&seed("sk/recovery")), time_lock: 86_400, custodian: None },
            heartbeat_quorum: 1, pins: vec![], members: vec![], access: vec![], pending: None, cause: None,
            private_commit: seed("private-commit"),
        };
        add_member(&mut g, o1.member, Role::Owner, vec![entry(&c_o1, 1)]);
        u.push(update(&g, vec![c_o1.clone()], &[&o1]));
        certs.push(c_o1);
        let team = g.hash();
        p.push(g);

        // v2: owner adds admins A and B (self-issued roots).
        let (c_a1, c_b1) = (cert_for(&a1, &a1, server, T0 + 2), cert_for(&b1, &b1, server, T0 + 2));
        let mut n = next(&p[0], &team);
        add_member(&mut n, a1.member, Role::Admin, vec![entry(&c_a1, 2)]);
        add_member(&mut n, b1.member, Role::Admin, vec![entry(&c_b1, 2)]);
        u.push(update(&n, vec![c_a1.clone(), c_b1.clone()], &[&o1]));
        certs.extend([c_a1, c_b1]);
        p.push(n);

        // v3: admin A adds member M, grants project access, pins an SPKI hash.
        let c_m1 = cert_for(&m1, &m1, server, T0 + 3);
        let mut n = next(&p[1], &team);
        add_member(&mut n, m1.member, Role::Member, vec![entry(&c_m1, 3)]);
        n.access = vec![AccessEntry { project_id: project, member_id: m1.member, level: Level::Contributor }];
        n.pins = vec![seed("spki-pin")];
        u.push(update(&n, vec![c_m1.clone()], &[&a1]));
        certs.push(c_m1);
        p.push(n);

        // v4: M adds its second device m2, issued by m1.
        let c_m2 = cert_for(&m2, &m1, server, T0 + 4);
        let mut n = next(&p[2], &team);
        add_device(&mut n, &m1.member, entry(&c_m2, 4));
        u.push(update(&n, vec![c_m2.clone()], &[&m1]));
        certs.push(c_m2);
        p.push(n);

        // v5: M revokes m2, plain revocation (compromisedSince = 5).
        let mut n = next(&p[3], &team);
        revoke(&mut n, &m2.device, 5);
        u.push(update(&n, vec![], &[&m1]));
        p.push(n);

        // v6: admin A removes member M (access entry goes with it).
        let mut n = next(&p[4], &team);
        remove_member(&mut n, &m1.member);
        n.access.clear();
        u.push(update(&n, vec![], &[&a1]));
        p.push(n);

        // Cascade v5': m1 compromised since v3 → its child m2 (added v4) must be revoked as well.
        let mut cascade_p5 = next(&p[3], &team);
        revoke(&mut cascade_p5, &m1.device, 3);
        revoke(&mut cascade_p5, &m2.device, 5);
        let cascade_u5 = update(&cascade_p5, vec![], &[&a1]);

        World { server, other_server, team, project, o1, a1, b1, m1, m2, x1, m3, p, u, certs, cascade_p5, cascade_u5 }
    }

    fn team_descriptor(&self) -> Descriptor {
        Descriptor { kind: GroupType::Team, team_id: self.team, project_id: None, members: vec![], owner: None, generation: 0 }
    }
}

fn chain_input(server: &Id16, team: Option<&H32>, ups: &[Vec<u8>]) -> J {
    let mut m = Map::new();
    m.insert("serverId".into(), json!(hex(server)));
    if let Some(t) = team { m.insert("teamId".into(), json!(hex(t))); }
    m.insert("updates".into(), J::Array(ups.iter().map(|x| json!(hex(x))).collect()));
    m.insert("now".into(), json!(NOW));
    J::Object(m)
}

// ======================= canonical.json =======================

fn canonical(w: &World) -> Vec<J> {
    let mut out = vec![];

    // invite.keys
    let k = seed("invite/k");
    let invite_id = id("invite/1");
    let ik = formats::invite_keys(&k, &invite_id).unwrap();
    let k_srv = util::hkdf32(&k, &invite_id, b"ombra/v1/invite/server");
    let k_adm = util::hkdf32(&k, &invite_id, b"ombra/v1/invite/admin");
    assert!(ik.k_srv == k_srv && ik.k_adm == k_adm && ik.verifier == util::sha256(&[&k_srv]));
    ok(&mut out, "invite-keys", "invite.keys", json!({"k": hex(&k), "inviteId": hex(&invite_id)}),
        json!({"kSrv": hex(&k_srv), "kAdm": hex(&k_adm), "verifier": hex(&util::sha256(&[&k_srv]))}));
    fail(&mut out, "invite-keys-short-secret", "invite.keys", json!({"k": hex(&k[..16]), "inviteId": hex(&invite_id)}), "invalid-input");

    // invite.bind: three devices, deliberately not in ascending deviceId order.
    let member = id("member/joiner");
    let mut devs: Vec<BindDevice> = (1..=3).map(|i| {
        let d = dev("joiner", &format!("j{i}"));
        BindDevice { device_id: d.device, identity_key: d.pk, mls_signature_key: d.mls, key_package: seed(&format!("kp/j{i}")).repeat(i) }
    }).collect();
    devs.sort_by(|a, b| b.device_id.cmp(&a.device_id));
    assert!(!devs.windows(2).all(|x| x[0].device_id < x[1].device_id), "bind input must be unsorted");
    let dev_json = |ds: &[&BindDevice]| J::Array(ds.iter().map(|d| json!({"deviceId": hex(&d.device_id), "identityKey": hex(&d.identity_key),
        "mlsSignatureKey": hex(&d.mls_signature_key), "keyPackage": hex(&d.key_package)})).collect());
    let mut sorted: Vec<&BindDevice> = devs.iter().collect();
    sorted.sort_by(|a, b| a.device_id.cmp(&b.device_id));
    let mut msg = dom("ombra/v1/invite-bind");
    msg.extend_from_slice(&invite_id);
    msg.extend_from_slice(&member);
    msg.push(sorted.len() as u8);
    for d in &sorted {
        msg.extend_from_slice(&d.device_id);
        msg.extend_from_slice(&d.identity_key);
        msg.extend_from_slice(&d.mls_signature_key);
        msg.extend_from_slice(&util::sha256(&[&d.key_package]));
    }
    let tag = util::hmac256(&k_adm, &msg);
    assert_eq!(formats::bind(&k_adm, &invite_id, &member, &devs).unwrap(), tag);
    let refs: Vec<&BindDevice> = devs.iter().collect();
    let bind_in = |ds: J| json!({"kAdm": hex(&k_adm), "inviteId": hex(&invite_id), "memberId": hex(&member), "devices": ds});
    ok(&mut out, "invite-bind-three-devices-unsorted", "invite.bind", bind_in(dev_json(&refs)), json!({"bind": hex(&tag), "message": hex(&msg)}));
    let mut msg1 = dom("ombra/v1/invite-bind");
    msg1.extend_from_slice(&invite_id);
    msg1.extend_from_slice(&member);
    msg1.push(1);
    msg1.extend_from_slice(&devs[0].device_id);
    msg1.extend_from_slice(&devs[0].identity_key);
    msg1.extend_from_slice(&devs[0].mls_signature_key);
    msg1.extend_from_slice(&util::sha256(&[&devs[0].key_package]));
    ok(&mut out, "invite-bind-one-device", "invite.bind", bind_in(dev_json(&refs[..1])),
        json!({"bind": hex(&util::hmac256(&k_adm, &msg1)), "message": hex(&msg1)}));
    fail(&mut out, "invite-bind-duplicate-device", "invite.bind", bind_in(dev_json(&[refs[0], refs[1], refs[0]])), "invalid-input");
    fail(&mut out, "invite-bind-no-devices", "invite.bind", bind_in(json!([])), "invalid-input");

    // heartbeat.canonical: three groups (variable-length ids), unsorted input.
    let groups = vec![
        HbGroup { group_id: seed("hb/g1").to_vec(), epoch: 42, epoch_proof: seed("hb/proof1") },
        HbGroup { group_id: id("hb/g2").to_vec(), epoch: 1, epoch_proof: seed("hb/proof2") },
        HbGroup { group_id: seed("hb/g3")[..20].to_vec(), epoch: 0x0102_0304_0506, epoch_proof: seed("hb/proof3") },
    ];
    let mut gs = groups.clone();
    gs.sort_by(|a, b| b.group_id.cmp(&a.group_id));
    let hb = Heartbeat { team_id: w.team, roster_version: 4, roster_hash: w.p[3].hash(), groups: gs.clone(), time: T0 + 1000, signer_device_id: w.a1.device };
    let mut asc = gs.clone();
    asc.sort_by(|a, b| a.group_id.cmp(&b.group_id));
    assert_ne!(asc, gs, "heartbeat input must be unsorted");
    let mut m = dom("ombra/v1/heartbeat");
    m.extend_from_slice(&hb.team_id);
    m.extend_from_slice(&be(hb.roster_version));
    m.extend_from_slice(&hb.roster_hash);
    m.extend_from_slice(&(asc.len() as u16).to_be_bytes());
    for g in &asc {
        m.extend_from_slice(&(g.group_id.len() as u16).to_be_bytes());
        m.extend_from_slice(&g.group_id);
        m.extend_from_slice(&be(g.epoch));
        m.extend_from_slice(&g.epoch_proof);
    }
    m.extend_from_slice(&be(hb.time));
    m.extend_from_slice(&hb.signer_device_id);
    assert_eq!(hb.signed_bytes().unwrap(), m);
    let hb_in = |gs: &[HbGroup]| json!({"teamId": hex(&hb.team_id), "rosterVersion": hb.roster_version, "rosterHash": hex(&hb.roster_hash),
        "groups": gs.iter().map(|g| json!({"groupId": hex(&g.group_id), "epoch": g.epoch, "epochProof": hex(&g.epoch_proof)})).collect::<Vec<_>>(),
        "time": hb.time, "signerDeviceId": hex(&hb.signer_device_id)});
    ok(&mut out, "heartbeat-three-groups-unsorted", "heartbeat.canonical", hb_in(&gs), json!({"signed": hex(&m)}));
    let mut m0 = dom("ombra/v1/heartbeat");
    m0.extend_from_slice(&hb.team_id);
    m0.extend_from_slice(&be(hb.roster_version));
    m0.extend_from_slice(&hb.roster_hash);
    m0.extend_from_slice(&0u16.to_be_bytes());
    m0.extend_from_slice(&be(hb.time));
    m0.extend_from_slice(&hb.signer_device_id);
    ok(&mut out, "heartbeat-no-groups", "heartbeat.canonical", hb_in(&[]), json!({"signed": hex(&m0)}));
    fail(&mut out, "heartbeat-duplicate-group", "heartbeat.canonical", hb_in(&[groups[0].clone(), groups[1].clone(), groups[0].clone()]), "invalid-input");

    // itemop.canonical: inner signature bytes for space and member audiences, several op types.
    let gdh = w.team_descriptor().group_id();
    let mk = |ty: OpType, signer: &Dev, item: &str, rev: u64, seq: u64, recipient: Id16| Header {
        team_id: w.team, group_descriptor_hash: gdh, epoch: 7, roster_version: 3, roster_hash: w.p[2].hash(),
        author_member_id: signer.member, signer_member_id: signer.member, signer_device_id: signer.device, op_type: ty as u8,
        item_id: id(&format!("item/{item}")), rev, seq, recipient_member_id: recipient, seq_head: seq,
    };
    let canon_cases: Vec<(&str, Header, Value)> = vec![
        ("itemop-canonical-create-space-mark", mk(OpType::Create, &w.a1, "mark", 1, 1, ZERO16), payload_mark(&w.team)),
        ("itemop-canonical-create-member-link", mk(OpType::Create, &w.a1, "link", 1, 2, w.m1.member),
            MapB::new().put("kind", t("link")).put("audience", MapB::new().put("member", b(&w.m1.member)).build())
                .put("createdAt", Value::U(T0 + 5)).put("body", MapB::new().put("url", t("https://example.org/ombra")).build()).build()),
        ("itemop-canonical-edit", mk(OpType::Edit, &w.a1, "mark", 2, 3, ZERO16),
            MapB::new().put("baseRev", Value::U(1)).put("delta", MapB::new().put("context", MapB::new().put("text", t("Обновлённый контекст")).build()).build()).build()),
        ("itemop-canonical-status-member", mk(OpType::Status, &w.m1, "link", 0, 1, w.m1.member),
            MapB::new().put("status", t("done")).put("note", t("ok")).build()),
        ("itemop-canonical-seq-skip", mk(OpType::SeqSkip, &w.m1, "skip", 0, 0, ZERO16),
            MapB::new().put("from", Value::U(2)).put("to", Value::U(4)).put("deviceId", b(&w.m1.device)).build()),
    ];
    for (name, h, payload) in canon_cases {
        let pc = cbor::encode(&payload);
        let ph = util::sha256(&[&pc]);
        let bytes = op_bytes(&h, &ph);
        assert_eq!(h.canonical_bytes(&ph), bytes);
        let mut hj = json!({"teamId": hex(&h.team_id), "groupDescriptorHash": hex(&h.group_descriptor_hash), "epoch": h.epoch,
            "rosterVersion": h.roster_version, "rosterHash": hex(&h.roster_hash), "authorMemberId": hex(&h.author_member_id),
            "signerMemberId": hex(&h.signer_member_id), "signerDeviceId": hex(&h.signer_device_id), "opType": h.op_type,
            "itemId": hex(&h.item_id), "rev": h.rev, "seq": h.seq, "seqHead": h.seq_head});
        if h.recipient_member_id != ZERO16 { hj["recipientMemberId"] = json!(hex(&h.recipient_member_id)); }
        ok(&mut out, name, "itemop.canonical", json!({"header": hj, "payload": input_json(&payload)}),
            json!({"canonical": hex(&bytes), "payloadHash": hex(&ph), "payloadCbor": hex(&pc)}));
    }

    // removal.canonical
    let n = RemovalNotice { team_id: w.team, member_id: w.m1.member, roster_version: 6, time: T0 + 6000, signer_device_id: w.a1.device };
    let mut m = dom("ombra/v1/removal-notice");
    m.extend_from_slice(&n.team_id);
    m.extend_from_slice(&n.member_id);
    m.extend_from_slice(&be(n.roster_version));
    m.extend_from_slice(&be(n.time));
    m.extend_from_slice(&n.signer_device_id);
    assert_eq!(n.signed_bytes(), m);
    ok(&mut out, "removal-notice", "removal.canonical", json!({"teamId": hex(&n.team_id), "memberId": hex(&n.member_id),
        "rosterVersion": n.roster_version, "time": n.time, "signerDeviceId": hex(&n.signer_device_id)}), json!({"signed": hex(&m)}));

    // hello.message
    let nonce = seed("hello/nonce");
    let time = T0 + 77;
    let mut m = dom("ombra/v1/hello");
    m.extend_from_slice(&w.server);
    m.extend_from_slice(&nonce);
    m.extend_from_slice(&w.m1.device);
    m.extend_from_slice(&be(time));
    assert_eq!(formats::hello_message(&w.server, &nonce, &w.m1.device, time), m);
    ok(&mut out, "hello-message", "hello.message", json!({"serverId": hex(&w.server), "nonce": hex(&nonce), "deviceId": hex(&w.m1.device), "time": time}),
        json!({"message": hex(&m)}));

    // envelope.message
    let body = cbor::encode(&MapB::new().put("type", t("presence")).put("at", Value::U(T0)).put("from", b(&w.m1.device)).build());
    ok(&mut out, "envelope-message", "envelope.message", json!({"body": hex(&body)}), json!({"message": hex(&[dom("ombra/v1/envelope"), body.clone()].concat())}));
    fail(&mut out, "envelope-non-canonical-body", "envelope.message", json!({"body": "a2616201616102"}), "cbor");

    // descriptor.group_id for every group type.
    let (lo, hi) = if w.a1.member < w.m1.member { (w.a1.member, w.m1.member) } else { (w.m1.member, w.a1.member) };
    let descs = [
        ("descriptor-team", w.team_descriptor()),
        ("descriptor-project", Descriptor { kind: GroupType::Project, team_id: w.team, project_id: Some(w.project), members: vec![], owner: None, generation: 1 }),
        ("descriptor-pair", Descriptor { kind: GroupType::Pair, team_id: w.team, project_id: None, members: vec![lo, hi], owner: None, generation: 0 }),
        ("descriptor-personal", Descriptor { kind: GroupType::Personal, team_id: w.team, project_id: None, members: vec![], owner: Some(w.m1.member), generation: 3 }),
    ];
    for (name, d) in descs {
        let mut mb = MapB::new().put("type", t(d.kind.as_str())).put("teamId", b(&d.team_id)).put("generation", Value::U(d.generation as u64));
        if let Some(p) = d.project_id { mb = mb.put("projectId", b(&p)); }
        if d.kind == GroupType::Pair { mb = mb.put("members", Value::A(d.members.iter().map(|x| b(x)).collect())); }
        if let Some(o) = d.owner { mb = mb.put("owner", b(&o)); }
        let c = cbor::encode(&mb.build());
        assert_eq!(d.canonical(), c);
        ok(&mut out, name, "descriptor.group_id", json!({"descriptor": serde_json::to_value(&d).unwrap()}),
            json!({"groupId": hex(&util::sha256(&[&c])), "canonical": hex(&c)}));
    }
    fail(&mut out, "descriptor-pair-members-descending", "descriptor.group_id",
        json!({"descriptor": {"type": "pair", "teamId": hex(&w.team), "members": [hex(&hi), hex(&lo)], "generation": 0}}), "invalid-input");
    fail(&mut out, "descriptor-team-with-owner", "descriptor.group_id",
        json!({"descriptor": {"type": "team", "teamId": hex(&w.team), "owner": hex(&lo), "generation": 0}}), "invalid-input");

    // roster.canonical: genesis and a projection with access, pins and a revocation.
    for (name, p) in [("roster-canonical-genesis", &w.p[0]), ("roster-canonical-v5-revocation", &w.p[4])] {
        let c = p.canonical();
        let signed = [dom("ombra/v1/roster"), c.clone()].concat();
        ok(&mut out, name, "roster.canonical", json!({"projection": serde_json::to_value(p).unwrap()}),
            json!({"canonical": hex(&c), "hash": hex(&util::sha256(&[&c])), "signed": hex(&signed)}));
    }

    // cert.parse: self-issued root and a device issued by another device of the member.
    for (name, c, issuer) in [("cert-self-issued-root", &w.certs[0], &w.o1), ("cert-issued-by-sibling", &w.certs[4], &w.m1)] {
        let bytes = c.to_bytes();
        assert!(util::ed25519_verify(&issuer.pk, &c.signed_bytes(), &c.sig), "cert signature over signed bytes");
        ok(&mut out, name, "cert.parse", json!({"cert": hex(&bytes)}),
            json!({"cert": serde_json::to_value(c).unwrap(), "hash": hex(&util::sha256(&[&bytes])), "signed": hex(&c.signed_bytes())}));
    }
    let Value::M(mut fields) = cbor::decode(&w.certs[0].to_bytes()).unwrap() else { unreachable!() };
    fields.push((t("extra"), Value::U(1)));
    fail(&mut out, "cert-unknown-field", "cert.parse", json!({"cert": hex(&cbor::encode(&Value::M(fields)))}), "cbor");

    // roster.private_commit
    let salt = seed("private/salt");
    let part = MapB::new()
        .put("names", MapB::new().put(&hex(&w.o1.member), t("Ольга")).put(&hex(&w.m1.member), t("Max")).build())
        .put("projects", Value::A(vec![MapB::new().put("id", b(&w.project)).put("name", t("Ombra")).build()]))
        .build();
    ok(&mut out, "roster-private-commit", "roster.private_commit", json!({"salt": hex(&salt), "privatePart": input_json(&part)}),
        json!({"privateCommit": hex(&util::sha256(&[&salt, &cbor::encode(&part)]))}));
    out
}

fn payload_mark(team: &H32) -> Value {
    MapB::new().put("kind", t("mark")).put("audience", MapB::new().put("space", b(team)).build()).put("createdAt", Value::U(T0 + 5))
        .put("anchor", MapB::new().put("web", MapB::new().put("url", t("https://example.org/a")).put("selector", t("#main > p:nth-child(2)"))
            .put("textQuote", t("the quoted text")).build()).build())
        .build()
}

/// Hand-built item-op inner signature bytes (§3.0, R5-03).
fn op_bytes(h: &Header, payload_hash: &H32) -> Vec<u8> {
    let mut m = dom("ombra/v1/item-op");
    m.extend_from_slice(&h.team_id);
    m.extend_from_slice(&h.group_descriptor_hash);
    m.extend_from_slice(&be(h.epoch));
    m.extend_from_slice(&be(h.roster_version));
    m.extend_from_slice(&h.roster_hash);
    m.extend_from_slice(&h.author_member_id);
    m.extend_from_slice(&h.signer_member_id);
    m.extend_from_slice(&h.signer_device_id);
    m.push(h.op_type);
    m.extend_from_slice(&h.item_id);
    m.extend_from_slice(&be(h.rev));
    m.extend_from_slice(&be(h.seq));
    m.extend_from_slice(&h.recipient_member_id);
    m.extend_from_slice(payload_hash);
    m
}

// ======================= cbor.json =======================

fn cbor_cases() -> Vec<J> {
    let mut out = vec![];
    let deep = |n: usize| format!("{}00", "81".repeat(n));
    let cases: Vec<(&str, String, bool)> = vec![
        ("cbor-uint-0", "00".into(), true),
        ("cbor-uint-23", "17".into(), true),
        ("cbor-uint-24", "1818".into(), true),
        ("cbor-uint-255", "18ff".into(), true),
        ("cbor-uint-256", "190100".into(), true),
        ("cbor-uint-65536", "1a00010000".into(), true),
        ("cbor-uint-2pow32", "1b0000000100000000".into(), true),
        ("cbor-uint-max", "1bffffffffffffffff".into(), true),
        ("cbor-negint-minus-1", "20".into(), true),
        ("cbor-negint-minus-25", "3818".into(), true),
        ("cbor-bytes-empty", "40".into(), true),
        ("cbor-bytes-3", "43010203".into(), true),
        ("cbor-text", "6161".into(), true),
        ("cbor-text-utf8", "62d096".into(), true),
        ("cbor-array-empty", "80".into(), true),
        ("cbor-array", "820102".into(), true),
        ("cbor-map-empty", "a0".into(), true),
        ("cbor-map-sorted", "a2616101616202".into(), true),
        ("cbor-map-shorter-key-first", "a261620162616102".into(), true),
        ("cbor-map-int-keys-before-text", "a201006161f6".into(), true),
        ("cbor-false-true-null", "83f4f5f6".into(), true),
        ("cbor-nesting-32", deep(32), true),
        ("cbor-non-minimal-uint-1byte", "1805".into(), false),
        ("cbor-non-minimal-uint-4byte", "1a00000100".into(), false),
        ("cbor-non-minimal-uint-8byte", "1b0000000000000000".into(), false),
        ("cbor-non-minimal-negint", "3800".into(), false),
        ("cbor-non-minimal-bytes-length", "5801ff".into(), false),
        ("cbor-non-minimal-array-length", "980100".into(), false),
        ("cbor-map-unsorted", "a2616201616102".into(), false),
        ("cbor-map-longer-key-first", "a26261610161620002".into(), false),
        ("cbor-map-duplicate-key", "a2616101616102".into(), false),
        ("cbor-indefinite-bytes", "5f4101ff".into(), false),
        ("cbor-indefinite-text", "7f6161ff".into(), false),
        ("cbor-indefinite-array", "9f01ff".into(), false),
        ("cbor-indefinite-map", "bf616101ff".into(), false),
        ("cbor-float16", "f90000".into(), false),
        ("cbor-float32", "fa3f800000".into(), false),
        ("cbor-float64", "fb3ff0000000000000".into(), false),
        ("cbor-undefined", "f7".into(), false),
        ("cbor-simple-value", "e0".into(), false),
        ("cbor-tag", "c001".into(), false),
        ("cbor-trailing-bytes", "0102".into(), false),
        ("cbor-truncated-uint", "1901".into(), false),
        ("cbor-truncated-bytes", "4301".into(), false),
        ("cbor-empty-input", "".into(), false),
        ("cbor-invalid-utf8", "61ff".into(), false),
        ("cbor-length-too-large", "5bffffffffffffffff".into(), false),
        ("cbor-reserved-info", "1c".into(), false),
        ("cbor-nesting-33", deep(33), false),
    ];
    for (name, h, canonical) in cases {
        let bytes = util::unhex(&h).unwrap();
        assert_eq!(cbor::decode(&bytes).is_ok(), canonical, "{name}: decoder verdict");
        if canonical { assert_eq!(cbor::encode(&cbor::decode(&bytes).unwrap()), bytes, "{name}: re-encoding"); }
        ok(&mut out, name, "cbor.check", json!({"bytes": h}), json!({"canonical": canonical}));
    }
    out
}

// ======================= roster.json =======================

fn roster_cases(w: &World) -> Vec<J> {
    let mut out = vec![];
    let head = |p: &Projection| json!({"teamId": hex(&w.team), "version": p.version, "hash": hex(&util::sha256(&[&p.canonical()]))});
    let s = &w.server;
    let chain = |ups: &[Vec<u8>]| chain_input(s, Some(&w.team), ups);
    let with = |n: usize, last: Vec<u8>| { let mut v = w.u[..n].to_vec(); v.push(last); chain_input(s, Some(&w.team), &v) };

    ok(&mut out, "roster-genesis-only", "roster.verify_chain", chain(&w.u[..1]), head(&w.p[0]));
    ok(&mut out, "roster-genesis-without-expected-team", "roster.verify_chain", chain_input(s, None, &w.u[..1]), head(&w.p[0]));
    ok(&mut out, "roster-chain-admins-member-device-revocation", "roster.verify_chain", chain(&w.u[..5]), head(&w.p[4]));
    ok(&mut out, "roster-chain-member-removed", "roster.verify_chain", chain(&w.u[..6]), head(&w.p[5]));
    ok(&mut out, "roster-chain-cascade-revocation", "roster.verify_chain", with(4, w.cascade_u5.clone()), head(&w.cascade_p5));
    // Re-sending a known version is idempotent.
    let mut dup = w.u[..5].to_vec();
    dup.push(w.u[3].clone());
    ok(&mut out, "roster-chain-known-version-resent", "roster.verify_chain", chain_input(s, Some(&w.team), &dup), head(&w.p[4]));

    fail(&mut out, "roster-genesis-hash-not-team-id", "roster.verify_chain", chain_input(s, Some(&seed("wrong-team")), &w.u[..1]), "roster-rejected");

    // Member M (role member) adds a new member X.
    let c_x1 = cert_for(&w.x1, &w.x1, w.server, T0 + 10);
    let mut n = next(&w.p[2], &w.team);
    add_member(&mut n, w.x1.member, Role::Member, vec![entry(&c_x1, 4)]);
    fail(&mut out, "roster-member-adds-member", "roster.verify_chain", with(3, update(&n, vec![c_x1.clone()], &[&w.m1])), "roster-rejected");

    // Admin A adds X directly as admin (only the owner appoints admins).
    let mut n = next(&w.p[2], &w.team);
    add_member(&mut n, w.x1.member, Role::Admin, vec![entry(&c_x1, 4)]);
    fail(&mut out, "roster-admin-appoints-new-admin", "roster.verify_chain", with(3, update(&n, vec![c_x1.clone()], &[&w.a1])), "roster-rejected");

    // Admin A promotes member M to admin.
    let mut n = next(&w.p[2], &w.team);
    set_role(&mut n, &w.m1.member, Role::Admin);
    fail(&mut out, "roster-admin-promotes-member-to-admin", "roster.verify_chain", with(3, update(&n, vec![], &[&w.a1])), "roster-rejected");

    // Admin A makes itself owner (owner demoted to admin), signed by one or by both admins.
    let mut n = next(&w.p[2], &w.team);
    n.owner = w.a1.member;
    set_role(&mut n, &w.a1.member, Role::Owner);
    set_role(&mut n, &w.o1.member, Role::Admin);
    fail(&mut out, "roster-admin-makes-itself-owner", "roster.verify_chain", with(3, update(&n, vec![], &[&w.a1])), "roster-rejected");
    fail(&mut out, "roster-admins-make-admin-owner-two-signatures", "roster.verify_chain", with(3, update(&n, vec![], &[&w.a1, &w.b1])), "roster-rejected");

    // Admin A removes the owner.
    let mut n = next(&w.p[2], &w.team);
    remove_member(&mut n, &w.o1.member);
    fail(&mut out, "roster-admin-removes-owner", "roster.verify_chain", with(3, update(&n, vec![], &[&w.a1])), "roster-rejected");

    // N2b: removed memberId M comes back (new root device m3).
    let c_m3 = cert_for(&w.m3, &w.m3, w.server, T0 + 11);
    let mut n = next(&w.p[5], &w.team);
    add_member(&mut n, w.m1.member, Role::Member, vec![entry(&c_m3, 7)]);
    fail(&mut out, "roster-readd-removed-member-id", "roster.verify_chain", with(6, update(&n, vec![c_m3], &[&w.a1])), "roster-rejected");

    // N2b: revoked deviceId m2 reappears as the root device of a new member X.
    let reuse = Dev { member: w.x1.member, device: w.m2.device, ..w.x1.clone() };
    let c_reuse = cert_for(&reuse, &reuse, w.server, T0 + 12);
    let mut n = next(&w.p[5], &w.team);
    add_member(&mut n, w.x1.member, Role::Member, vec![entry(&c_reuse, 7)]);
    fail(&mut out, "roster-readd-revoked-device-id", "roster.verify_chain", with(6, update(&n, vec![c_reuse], &[&w.a1])), "roster-rejected");

    // R2-10 cascade: m1 revoked compromised since v3, its child m2 (added v4) left active.
    let mut n = next(&w.p[3], &w.team);
    revoke(&mut n, &w.m1.device, 3);
    fail(&mut out, "roster-cascade-child-not-revoked", "roster.verify_chain", with(4, update(&n, vec![], &[&w.a1])), "roster-rejected");

    // Version gap: v5 directly after v3.
    let mut gap = w.u[..3].to_vec();
    gap.push(w.u[4].clone());
    fail(&mut out, "roster-version-gap", "roster.verify_chain", chain_input(s, Some(&w.team), &gap), "need-roster");

    // Wrong prevHash on v4.
    let c_m2 = w.certs[4].clone();
    let mut n = w.p[3].clone();
    n.prev_hash = Some(seed("wrong-prev-hash"));
    fail(&mut out, "roster-wrong-prev-hash", "roster.verify_chain", with(3, update(&n, vec![c_m2], &[&w.m1])), "roster-rejected");

    // Tampered signature on v4.
    let mut t4 = RosterUpdate::parse(&w.u[3]).unwrap();
    t4.signatures[0].sig[5] ^= 0x01;
    fail(&mut out, "roster-tampered-signature", "roster.verify_chain", with(3, t4.to_bytes()), "bad-signature");

    // Device certificate issued for another serverId.
    let c_other = cert_for(&w.m2, &w.m1, w.other_server, T0 + 4);
    let mut n = next(&w.p[2], &w.team);
    add_device(&mut n, &w.m1.member, entry(&c_other, 4));
    fail(&mut out, "roster-cert-for-another-server", "roster.verify_chain", with(3, update(&n, vec![c_other], &[&w.m1])), "roster-rejected");

    // The same valid chain verified for another serverId (genesis cert binds the server).
    fail(&mut out, "roster-chain-for-another-server", "roster.verify_chain", chain_input(&w.other_server, Some(&w.team), &w.u[..3]), "roster-rejected");
    out
}

// ======================= itemop.json =======================

struct OpB<'a> { w: &'a World, name: &'a str, signer: &'a Dev, v: u64, ty: u8, payload: Value, seq: u64, recipient: Id16 }

impl OpB<'_> {
    fn header(&self) -> Header {
        let w = self.w;
        Header {
            team_id: w.team, group_descriptor_hash: w.team_descriptor().group_id(), epoch: 9, roster_version: self.v,
            roster_hash: w.p.get(self.v as usize - 1).map(|p| p.hash()).unwrap_or(seed("unknown-version-hash")),
            author_member_id: self.signer.member, signer_member_id: self.signer.member, signer_device_id: self.signer.device,
            op_type: self.ty, item_id: id(&format!("item/{}", self.name)), rev: if self.ty == 1 { 1 } else { 0 }, seq: self.seq,
            recipient_member_id: self.recipient, seq_head: self.seq,
        }
    }
    fn sign(&self, header: Header) -> SignedOp {
        let mut op = SignedOp { op_id: id(&format!("op/{}", self.name)), header, payload: self.payload.clone(), sig: [0; 64] };
        let sb = op_bytes(&op.header, &util::sha256(&[&cbor::encode(&op.payload)]));
        assert_eq!(op.signed_bytes(), sb);
        op.sig = util::ed25519_sign(&self.signer.sk, &sb);
        op
    }
    fn op(&self) -> SignedOp { self.sign(self.header()) }
}

fn itemop_cases(w: &World) -> Vec<J> {
    let mut out = vec![];
    let ob = |name: &'static str, signer: &'static str, v: u64, ty: u8, payload: Value| -> OpB {
        let s = match signer { "a1" => &w.a1, "m1" => &w.m1, "m2" => &w.m2, _ => unreachable!() };
        let seq = if ty == 8 || ty == 9 { 0 } else { 1 };
        OpB { w, name, signer: s, v, ty, payload, seq, recipient: ZERO16 }
    };
    let key = Value::A(vec![b(&w.a1.member), b(&id("item/itemop-parse-create-mark"))]);
    let link = |url: &str| MapB::new().put("kind", t("link")).put("audience", MapB::new().put("member", b(&w.m1.member)).build())
        .put("createdAt", Value::U(T0 + 20)).put("body", MapB::new().put("url", t(url)).build()).build();
    let collection = |title: &str| MapB::new().put("kind", t("collection")).put("audience", MapB::new().put("space", b(&w.team)).build())
        .put("createdAt", Value::U(T0 + 22)).put("body", MapB::new().put("itemKeys", Value::A(vec![key.clone()])).put("title", t(title)).build()).build();

    // ---- itemop.parse: valid op of every type ----
    let mut member_link = ob("itemop-parse-create-link-member", "a1", 3, 1, link("https://example.org/l"));
    member_link.recipient = w.m1.member;
    let valid: Vec<OpB> = vec![
        ob("itemop-parse-create-mark", "a1", 3, 1, payload_mark(&w.team)),
        member_link,
        ob("itemop-parse-create-snapshot-context-attachments", "a1", 3, 1, MapB::new().put("kind", t("snapshot"))
            .put("audience", MapB::new().put("space", b(&w.project)).build()).put("createdAt", Value::U(T0 + 21))
            .put("context", MapB::new().put("text", t(&"к".repeat(2000))).put("selection", t("sel")).put("blobRefs", Value::A(vec![t("blob:1")])).build())
            .put("body", MapB::new().put("blobRef", t("blob:snap")).build())
            .put("attachments", Value::A(vec![MapB::new().put("blobRef", t("blob:att")).put("key", b(&seed("att-key"))).build()])).build()),
        ob("itemop-parse-edit", "a1", 3, 2, MapB::new().put("baseRev", Value::U(1))
            .put("delta", MapB::new().put("body", MapB::new().put("url", t("https://example.org/b")).build()).build()).build()),
        ob("itemop-parse-delete", "a1", 3, 3, MapB::new().build()),
        ob("itemop-parse-collection-add", "a1", 3, 4, MapB::new().put("itemKey", key.clone()).build()),
        ob("itemop-parse-collection-remove", "a1", 3, 5, MapB::new().put("itemKey", key.clone()).build()),
        ob("itemop-parse-status-note-280-chars", "m1", 3, 6, MapB::new().put("status", t("seen")).put("note", t(&"ж".repeat(280))).build()),
        ob("itemop-parse-share", "a1", 3, 7, MapB::new().put("shareId", b(&id("share/1"))).put("targetMemberId", b(&w.m1.member))
            .put("targetDeviceId", b(&w.m1.device)).put("scopes", Value::A(vec![b(&w.team), b(&w.project)]))
            .put("count", Value::U(3)).put("sha256", b(&seed("share/archive"))).build()),
        ob("itemop-parse-seq-head", "m1", 3, 8, MapB::new().put("lastSeq", Value::U(17)).build()),
        ob("itemop-parse-seq-skip", "m1", 3, 9, MapB::new().put("from", Value::U(2)).put("to", Value::U(4)).put("deviceId", b(&w.m1.device)).build()),
        ob("itemop-parse-create-collection-title-200-chars", "a1", 3, 1, collection(&"г".repeat(200))),
    ];
    for o in &valid {
        let op = o.op();
        let ty = OpType::from_u8(o.ty).unwrap();
        ok(&mut out, o.name, "itemop.parse", json!({"signedOp": hex(&op.to_bytes())}),
            json!({"opType": ty.name(), "opId": hex(&op.op_id), "signed": hex(&op_bytes(&op.header, &util::sha256(&[&cbor::encode(&op.payload)]))),
                "payload": replica::cbor_json(&op.payload)}));
    }

    // ---- itemop.parse: rejected ops ----
    let no_anchor = MapB::new().put("kind", t("mark")).put("audience", MapB::new().put("space", b(&w.team)).build()).put("createdAt", Value::U(T0)).build();
    let both = MapB::new().put("kind", t("link")).put("audience", MapB::new().put("space", b(&w.team)).put("member", b(&w.m1.member)).build())
        .put("createdAt", Value::U(T0)).put("body", MapB::new().put("url", t("https://example.org")).build()).build();
    let rejected: Vec<OpB> = vec![
        ob("itemop-parse-unknown-op-type-reply", "a1", 3, 11, MapB::new().put("text", t("a reply")).build()),
        ob("itemop-parse-op-type-zero", "a1", 3, 0, MapB::new().build()),
        ob("itemop-parse-mark-without-anchor", "a1", 3, 1, no_anchor),
        ob("itemop-parse-unknown-kind", "a1", 3, 1, MapB::new().put("kind", t("reply")).put("audience", MapB::new().put("space", b(&w.team)).build()).put("createdAt", Value::U(T0)).build()),
        ob("itemop-parse-note-281-chars", "m1", 3, 6, MapB::new().put("status", t("done")).put("note", t(&"ж".repeat(281))).build()),
        ob("itemop-parse-context-2001-chars", "a1", 3, 2, MapB::new().put("baseRev", Value::U(1))
            .put("delta", MapB::new().put("context", MapB::new().put("text", t(&"к".repeat(2001))).build()).build()).build()),
        ob("itemop-parse-unknown-payload-field", "m1", 3, 6, MapB::new().put("status", t("done")).put("reply", t("thread")).build()),
        ob("itemop-parse-audience-space-and-member", "a1", 3, 1, both),
        ob("itemop-parse-unknown-status", "m1", 3, 6, MapB::new().put("status", t("liked")).build()),
        ob("itemop-parse-empty-edit", "a1", 3, 2, MapB::new().put("baseRev", Value::U(1)).put("delta", MapB::new().build()).build()),
        ob("itemop-parse-bad-seq-skip-range", "m1", 3, 9, MapB::new().put("from", Value::U(5)).put("to", Value::U(4)).put("deviceId", b(&w.m1.device)).build()),
        ob("itemop-parse-larger-than-64k", "a1", 3, 1, link(&format!("https://example.org/{}", "x".repeat(66_000)))),
        ob("itemop-parse-collection-title-201-chars", "a1", 3, 1, collection(&"г".repeat(201))),
        ob("itemop-parse-collection-title-empty", "a1", 3, 1, collection("")),
    ];
    for o in &rejected {
        let bytes = o.op().to_bytes();
        if o.name.ends_with("64k") { assert!(bytes.len() > 64 * 1024); }
        fail(&mut out, o.name, "itemop.parse", json!({"signedOp": hex(&bytes)}), "op-rejected");
    }
    let mut trailing = valid[0].op().to_bytes();
    trailing.push(0);
    fail(&mut out, "itemop-parse-trailing-bytes", "itemop.parse", json!({"signedOp": hex(&trailing)}), "op-rejected");

    // ---- itemop.verify ----
    let verify_in = |ups: &[Vec<u8>], op: &SignedOp| {
        let mut j = chain_input(&w.server, Some(&w.team), ups);
        j["signedOp"] = json!(hex(&op.to_bytes()));
        j
    };
    let verdict = |op: &SignedOp| json!({"valid": true, "opType": OpType::from_u8(op.header.op_type).unwrap().name(),
        "rosterVersion": op.header.roster_version, "signerDeviceId": hex(&op.header.signer_device_id)});
    let main5 = &w.u[..5];
    let status = || MapB::new().put("status", t("done")).build();

    let op = ob("itemop-verify-admin-create-v3", "a1", 3, 1, payload_mark(&w.team)).op();
    ok(&mut out, "itemop-verify-admin-create-v3", "itemop.verify", verify_in(main5, &op), verdict(&op));

    // Plain revocation (compromisedSince = 5): m2's op signed at v4 stays valid.
    let op = ob("itemop-verify-plain-revocation-earlier-op-valid", "m2", 4, 6, status()).op();
    ok(&mut out, "itemop-verify-plain-revocation-earlier-op-valid", "itemop.verify", verify_in(main5, &op), verdict(&op));

    let op = ob("itemop-verify-signer-revoked-in-version", "m2", 5, 6, status()).op();
    fail(&mut out, "itemop-verify-signer-revoked-in-version", "itemop.verify", verify_in(main5, &op), "op-rejected");

    let bo = ob("itemop-verify-tampered-payload", "m1", 4, 6, status());
    let mut op = bo.op();
    op.payload = MapB::new().put("status", t("dismissed")).build();
    fail(&mut out, "itemop-verify-tampered-payload", "itemop.verify", verify_in(main5, &op), "bad-signature");

    let bo = ob("itemop-verify-signer-member-mismatch", "m1", 4, 6, status());
    let mut h = bo.header();
    h.signer_member_id = w.a1.member;
    h.author_member_id = w.a1.member;
    fail(&mut out, "itemop-verify-signer-member-mismatch", "itemop.verify", verify_in(main5, &bo.sign(h)), "op-rejected");

    let op = ob("itemop-verify-unknown-roster-version", "m1", 9, 6, status()).op();
    fail(&mut out, "itemop-verify-unknown-roster-version", "itemop.verify", verify_in(main5, &op), "need-roster");

    let bo = ob("itemop-verify-roster-hash-mismatch", "m1", 4, 6, status());
    let mut h = bo.header();
    h.roster_hash = w.p[2].hash();
    fail(&mut out, "itemop-verify-roster-hash-mismatch", "itemop.verify", verify_in(main5, &bo.sign(h)), "op-rejected");

    let bo = ob("itemop-verify-another-team", "m1", 4, 6, status());
    let mut h = bo.header();
    h.team_id = seed("another-team");
    fail(&mut out, "itemop-verify-another-team", "itemop.verify", verify_in(main5, &bo.sign(h)), "op-rejected");

    let op = ob("itemop-verify-unknown-op-type", "m1", 4, 11, MapB::new().build()).op();
    fail(&mut out, "itemop-verify-unknown-op-type", "itemop.verify", verify_in(main5, &op), "op-rejected");

    // R2-10 cascade: m2 (issued by m1 in v4) is listed active at v4, but v5 revokes m1 with
    // compromisedSince = 3 (and m2): m2's v4 signature, and m1's own at v4, are refused.
    let mut cascade = w.u[..4].to_vec();
    cascade.push(w.cascade_u5.clone());
    let op = ob("itemop-verify-cascade-child-of-compromised-issuer", "m2", 4, 6, status()).op();
    fail(&mut out, "itemop-verify-cascade-child-of-compromised-issuer", "itemop.verify", verify_in(&cascade, &op), "op-rejected");
    let op = ob("itemop-verify-compromised-signer-before-revocation", "m1", 4, 6, status()).op();
    fail(&mut out, "itemop-verify-compromised-signer-before-revocation", "itemop.verify", verify_in(&cascade, &op), "op-rejected");
    // ... while the same op verified against the chain without v5 is still valid (no knowledge yet).
    ok(&mut out, "itemop-verify-before-compromise-known", "itemop.verify", verify_in(&w.u[..4], &op), verdict(&op));
    // An admin's op at the cascade version is unaffected.
    // rosterHash of v5 differs between the main and the cascade chain: point it at the cascade v5.
    let bo = ob("itemop-verify-admin-after-cascade", "a1", 5, 6, status());
    let mut h = bo.header();
    h.roster_hash = w.cascade_p5.hash();
    let op = bo.sign(h);
    ok(&mut out, "itemop-verify-admin-after-cascade", "itemop.verify", verify_in(&cascade, &op), verdict(&op));
    out
}
