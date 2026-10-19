//! Test-only attacker helpers (cargo feature `test-attacks`): build commits, groups, Welcomes and
//! item ops WITHOUT the local validator, to prove that *receivers* reject them. Never compiled
//! into release artifacts.

use openmls::prelude::{tls_codec::*, *};
use openmls::schedule::PreSharedKeyId;
use openmls_basic_credential::SignatureKeyPair;
use openmls_traits::OpenMlsProvider;
use serde_json::{json, Value as J};

use crate::api::Args;
use crate::descriptor::Descriptor;
use crate::itemop::SignedOp;
use crate::mls::{self, load_group, signer, CS, DESC_EXT, ROSTER_EXT};
use crate::replica;
use crate::state::{GroupMeta, Provider, State};
use crate::util::{self, e, err, hex, Code, Res};

fn m<E: std::fmt::Debug>(x: E) -> util::Error { e(Code::Mls, format!("{x:?}")) }

pub fn call(st: &mut State, p: &Provider, db: &crate::store::Db, op: &str, a: &Args) -> Res<J> {
    match op {
        "attack.store_psk" => {
            PreSharedKeyId::external(a.bytes("pskId")?, vec![0; 32]).store(p, &a.bytes("secret")?).map_err(m)?;
            Ok(json!({}))
        }
        "attack.group_info" => {
            let g = load_group(p, &a_gid(a)?)?;
            let gi = g.export_group_info(p.crypto(), &signer(st, p)?, true).map_err(m)?;
            Ok(json!({"groupInfo": hex(&gi.to_bytes().map_err(m)?)}))
        }
        "attack.external_join" => {
            let msg = MlsMessageIn::tls_deserialize_exact(&a.bytes("groupInfo")?).map_err(m)?;
            let MlsMessageBodyIn::GroupInfo(vgi) = msg.extract() else { return err(Code::InvalidInput, "not a GroupInfo") };
            let (g, bundle) = MlsGroup::external_commit_builder()
                .with_config(mls::join_config())
                .build_group(p, vgi, mls::credential(st)?).map_err(m)?
                .leaf_node_parameters(LeafNodeParameters::builder().with_capabilities(mls::caps()).build())
                .load_psks(p.storage()).map_err(m)?
                .build(p.rand(), p.crypto(), &signer(st, p)?, |_| true).map_err(m)?
                .finalize(p).map_err(m)?;
            let gid: [u8; 32] = g.group_id().as_slice().try_into().map_err(|_| e(Code::InvalidInput, "gid"))?;
            let d = g.extensions().unknown(DESC_EXT).map(|u| u.0.clone()).unwrap_or_default();
            insert_meta(st, db, gid, &d)?;
            Ok(json!({"commit": hex(&bundle.commit().to_bytes().map_err(m)?), "groupId": hex(&gid)}))
        }
        "attack.create_group" => {
            let gid = util::h32(&a.bytes("groupId")?)?;
            let d = a.bytes("descriptor")?;
            let cfg = MlsGroupCreateConfig::builder().ciphersuite(CS).use_ratchet_tree_extension(true)
                .wire_format_policy(mls::WIRE_POLICY).capabilities(mls::caps())
                .with_group_context_extensions(mls::gc_extensions(&a.bytes("rosterHash")?, &d)?).build();
            MlsGroup::new_with_group_id(p, &signer(st, p)?, &cfg, GroupId::from_slice(&gid), mls::credential(st)?).map_err(m)?;
            insert_meta(st, db, gid, &d)?;
            Ok(json!({"groupId": hex(&gid)}))
        }
        "attack.commit" => {
            let gid = a_gid(a)?;
            let mut g = load_group(p, &gid)?;
            let s = signer(st, p)?;
            let mode = a.opt_u("updateMode").unwrap_or(0);
            let bundle = if mode != 0 {
                let ns = SignatureKeyPair::new(CS.signature_algorithm()).map_err(m)?;
                ns.store(p.storage()).map_err(m)?;
                let id = if mode == 2 { a.bytes("newId")? } else { st.device()?.device_id.to_vec() };
                let cwk = CredentialWithKey { credential: BasicCredential::new(id).into(), signature_key: ns.to_public_vec().into() };
                g.self_update_with_new_signer(p, &s, NewSignerBundle { signer: &ns, credential_with_key: cwk.clone() },
                    LeafNodeParameters::builder().with_credential_with_key(cwk).build()).map_err(m)?
            } else {
                let mut kps = vec![];
                for kp in a.hex_list("adds")? {
                    kps.push(KeyPackageIn::tls_deserialize_exact(&kp).map_err(m)?.validate(p.crypto(), ProtocolVersion::Mls10).map_err(m)?);
                }
                let mut leaves = vec![];
                for d in a.hex_list("removeDevices")? {
                    leaves.push(g.members().find(|x| x.credential.serialized_content() == d.as_slice()).ok_or_else(|| e(Code::NotFound, "leaf"))?.index);
                }
                let gce = if a.raw("rosterHash").is_some() || a.raw("descriptor").is_some() {
                    let gc = g.extensions();
                    let rh = match a.raw("rosterHash").is_some() { true => a.bytes("rosterHash")?, false => gc.unknown(ROSTER_EXT).unwrap().0.clone() };
                    let d = match a.raw("descriptor").is_some() { true => a.bytes("descriptor")?, false => gc.unknown(DESC_EXT).unwrap().0.clone() };
                    Some(mls::gc_extensions(&rh, &d)?)
                } else { None };
                let mut b = g.commit_builder().force_self_update(!a.flag("noPath")).consume_proposal_store(false).propose_adds(kps).propose_removals(leaves);
                if let Some(x) = gce { b = b.propose_group_context_extensions(x).map_err(m)?; }
                if a.raw("pskId").is_some() {
                    let id = PreSharedKeyId::external(a.bytes("pskId")?, util::random::<32>().to_vec());
                    b = b.add_proposal(Proposal::PreSharedKey(Box::new(PreSharedKeyProposal::new(id))));
                }
                b.load_psks(p.storage()).map_err(m)?.build(p.rand(), p.crypto(), &s, |_| true).map_err(m)?.stage_commit(p).map_err(m)?
            };
            let kinds: Vec<String> = g.pending_commit().map(|sc| sc.queued_proposals().map(|q| format!("{:?}", q.proposal().proposal_type())).collect()).unwrap_or_default();
            g.merge_pending_commit(p).map_err(m)?;
            Ok(json!({"commit": hex(&bundle.commit().to_bytes().map_err(m)?), "proposals": kinds,
                "welcome": bundle.welcome().map(|w| MlsMessageOut::from_welcome(w.clone(), ProtocolVersion::Mls10).to_bytes()).transpose().map_err(m)?.map(|b| hex(&b))}))
        }
        "attack.sign_op" => {
            let d = st.device()?;
            let h = crate::api::header_from(&a.sub("header")?)?;
            let mut op = SignedOp { op_id: util::random::<16>(), header: h, payload: replica::json_cbor(a.raw("payload").unwrap_or(&json!({})))?, sig: [0; 64] };
            if a.raw("opId").is_some() { op.op_id = util::id16(&a.bytes("opId")?)?; }
            op.sig = util::ed25519_sign(&d.identity_sk, &op.signed_bytes());
            if a.flag("badSig") { op.sig[0] ^= 1; }
            Ok(json!({"op": hex(&op.to_bytes())}))
        }
        "attack.propose_update" => {
            // Standalone Update proposal carrying a foreign credential (R2-14).
            let gid = a_gid(a)?;
            let mut g = load_group(p, &gid)?;
            let ns = SignatureKeyPair::new(CS.signature_algorithm()).map_err(m)?;
            ns.store(p.storage()).map_err(m)?;
            let cwk = CredentialWithKey { credential: BasicCredential::new(a.bytes("newId")?).into(), signature_key: ns.to_public_vec().into() };
            let (msg, _) = g.propose_self_update_with_new_signer(p, &signer(st, p)?, NewSignerBundle { signer: &ns, credential_with_key: cwk.clone() },
                LeafNodeParameters::builder().with_credential_with_key(cwk).build()).map_err(m)?;
            Ok(json!({"proposal": hex(&msg.to_bytes().map_err(m)?)}))
        }
        "attack.reinit_commit" => {
            // R2-14: a commit whose only proposal is an inline ReInit, signed by this member's real leaf
            // key over the current GroupContext. OpenMLS' commit builder silently drops ReInit, so the
            // message is assembled with the "frankenstein" codec. The membership tag and confirmation
            // tag are dummies (an insider cannot export the membership key through the public API):
            // members reject it at the MLS layer, the server's PublicGroup (which can verify neither tag)
            // reaches the proposal whitelist of `mls::validate_commit`.
            use openmls::test_utils::frankenstein::*;
            let gid = a_gid(a)?;
            let g = load_group(p, &gid)?;
            let gc = g.export_group_context().tls_serialize_detached().map_err(m)?;
            let reinit = FrankenReInitProposal { group_id: gid.to_vec().into(), version: 1, ciphersuite: u16::from(CS), extensions: vec![] };
            let content = FrankenFramedContent {
                group_id: gid.to_vec().into(), epoch: g.epoch().as_u64(), sender: FrankenSender::Member(g.own_leaf_index().u32()),
                authenticated_data: vec![].into(),
                body: FrankenFramedContentBody::Commit(FrankenCommit { proposals: vec![FrankenProposalOrRef::Proposal(FrankenProposal::ReInit(reinit))], path: None }),
            };
            // FramedContentTBS = version u16 ‖ wire_format u16 (PublicMessage) ‖ FramedContent ‖ GroupContext.
            let mut tbs = vec![0, 1, 0, 1];
            tbs.extend(content.tls_serialize_detached().map_err(m)?);
            tbs.extend(gc);
            let auth = FrankenFramedContentAuthData { signature: sign_with_label(&signer(st, p)?, b"FramedContentTBS", &tbs).into(),
                confirmation_tag: Some(vec![0u8; 32].into()) };
            let msg = FrankenMlsMessage { version: 1, body: FrankenMlsMessageBody::PublicMessage(FrankenPublicMessage { content, auth, membership_tag: Some(vec![0u8; 32].into()) }) };
            Ok(json!({"commit": hex(&msg.tls_serialize_detached().map_err(m)?)}))
        }
        "attack.encrypt" => {
            let gid = a_gid(a)?;
            let mut g = load_group(p, &gid)?;
            let msg = g.create_message(p, &signer(st, p)?, &a.bytes("plaintext")?).map_err(m)?;
            Ok(json!({"message": hex(&msg.to_bytes().map_err(m)?), "epoch": g.epoch().as_u64()}))
        }
        _ => err(Code::InvalidInput, format!("unknown attack op '{op}'")),
    }
}

fn a_gid(a: &Args) -> Res<[u8; 32]> { util::h32(&a.bytes("groupId")?) }

/// The attacker may not hold the team: its group row still needs a team key in state.db.
fn insert_meta(st: &mut State, db: &crate::store::Db, gid: [u8; 32], d: &[u8]) -> Res<()> {
    let desc = Descriptor::parse(d).ok();
    let team = desc.as_ref().map(|x| x.team_id).unwrap_or([0; 32]);
    if !db.is_unlocked(&team) { db.add_team_key(&team, util::random::<32>())?; }
    st.groups.insert(hex(&gid), GroupMeta {
        group_id: gid, team_id: desc.as_ref().map(|x| x.team_id).unwrap_or([0; 32]), descriptor: d.to_vec(),
        kind: desc.map(|x| x.kind).unwrap_or(crate::descriptor::GroupType::Team), roster_version: 0, closed: false, evicted: false,
        weak_fs: Default::default(), track: Default::default(),
    });
    Ok(())
}
