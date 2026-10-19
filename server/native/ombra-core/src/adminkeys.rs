//! Invite secrets among admins (§3.4 п.4, R2-05): `k_adm` of an invite stays on the device that created it and
//! is handed to the team's other owner/admin devices only inside the team's **admin group** (descriptor type
//! `admin`; its leaves are exactly the active owner/admin devices of the roster). A member without the admin
//! role never holds `k_adm`; when an admin is removed or demoted, the admin group is rotated (its leaves are no
//! longer admissible) and the invites it created stop verifying (`invite.verify_join` checks the inviter at the
//! head), so its links can no longer bring anyone in.
//!
//! Wire form of the MLS application message: `MAGIC ‖ canonical CBOR {invites: [{inviteId, kAdm, role, maxUses,
//! expiresAt, inviterDevice}]}`. The prefix cannot start a SignedOp, so the incoming path tries [`try_open`] early.

use serde_json::{json, Value as J};

use crate::cbor::{self, b, t, Fields, MapB, Value};
use crate::descriptor::GroupType;
use crate::ensure;
use crate::state::{InviteSecret, Provider, State};
use crate::util::{e, err, hex, unhex, Code, Id16, Res, H32};

pub const MAGIC: &[u8] = b"ombra/v1/admin-keys\0";

fn encode(state: &State, team: &H32, now: u64) -> Res<Vec<u8>> {
    let t_ = state.team(team)?;
    let list: Vec<Value> = t_.invites.iter().filter(|(_, s)| s.expires_at > now).map(|(id, s)| {
        let id = unhex(id).unwrap_or_default();
        MapB::new().put("inviteId", b(&id)).put("kAdm", b(&s.k_adm)).put("role", t(&s.role))
            .put("maxUses", Value::U(s.max_uses)).put("expiresAt", Value::U(s.expires_at))
            .put("inviterDevice", b(&s.inviter_device.unwrap_or([0; 16]))).build()
    }).collect();
    Ok([MAGIC, &cbor::encode(&MapB::new().put("invites", Value::A(list)).build())].concat())
}

/// `adminkeys.seal {groupId, now}`: the unexpired invite secrets this device holds for the group's team, as an
/// MLS application message of the team's admin group (after creating an invite, and to a newly added admin).
pub fn seal(state: &State, p: &Provider, gid: &H32, now: u64) -> Res<J> {
    let meta = state.group(gid)?;
    ensure!(meta.kind == GroupType::Admin, Code::InvalidInput, "invite secrets travel only in the admin group");
    ensure!(!meta.closed && !meta.evicted, Code::Frozen, "group closed");
    let me = state.device()?.device_id;
    let head = state.team(&meta.team_id)?.chain.head();
    ensure!(head.active_device(&me).is_some_and(|(m, _)| m.role.is_admin()), Code::InvalidInput, "only an owner/admin device sends invite secrets");
    let wire = encode(state, &meta.team_id, now)?;
    let count = state.team(&meta.team_id)?.invites.values().filter(|s| s.expires_at > now).count();
    let mut group = crate::mls::load_group(p, gid)?;
    group.set_aad(meta.roster_version.to_be_bytes().to_vec());
    let msg = group.create_message(p, &crate::mls::signer(state, p)?, &wire).map_err(|x| e(Code::Mls, format!("{x:?}")))?;
    Ok(json!({"message": hex(&msg.to_bytes().map_err(|x| e(Code::Mls, format!("{x:?}")))?), "invites": count}))
}

/// Incoming application message: `None` when it is not an admin-keys message. Accepted only in an admin group,
/// from a device that is an active owner/admin at this device's verified head, and only by an owner/admin device.
pub fn try_open(state: &mut State, gid: &H32, sender: &Id16, plaintext: &[u8]) -> Option<Res<J>> {
    let body = plaintext.strip_prefix(MAGIC)?;
    Some((|| {
        let meta = state.group(gid)?;
        ensure!(meta.kind == GroupType::Admin, Code::OpRejected, "invite secrets outside the admin group");
        let team = meta.team_id;
        let me = state.device()?.device_id;
        let head = state.team(&team)?.chain.head();
        ensure!(head.active_device(sender).is_some_and(|(m, _)| m.role.is_admin()), Code::OpRejected, "invite secrets from a device that is not owner/admin");
        ensure!(head.active_device(&me).is_some_and(|(m, _)| m.role.is_admin()), Code::OpRejected, "this device is not owner/admin");
        let mut f = Fields::new(cbor::decode(body)?, "admin-keys")?;
        let list = f.req("invites")?.array()?;
        f.finish()?;
        let mut added = 0;
        for v in list {
            let mut x = Fields::new(v, "invite secret")?;
            let id: Id16 = x.req("inviteId")?.fixed()?;
            let s = InviteSecret {
                k_adm: x.req("kAdm")?.fixed()?, role: x.req("role")?.text()?.to_string(), max_uses: x.req("maxUses")?.u()?,
                expires_at: x.req("expiresAt")?.u()?, inviter_device: Some(x.req("inviterDevice")?.fixed()?),
            };
            x.finish()?;
            if s.role != "member" && s.role != "admin" { return err(Code::OpRejected, "bad invite role"); }
            let t_ = state.team_mut(&team)?;
            if !t_.invites.contains_key(&hex(&id)) {
                t_.invites.insert(hex(&id), s);
                added += 1;
            }
        }
        Ok(json!({"kind": "admin-keys", "added": added}))
    })())
}

/// After this device applied a roster version: if it is no longer an active owner/admin device of `team`, the admin
/// group's MLS state and every invite secret it held are dropped (a removed leaf never gets the rotating commit, so
/// the roster is what tells it).
pub fn after_roster(state: &mut State, p: &Provider, team: &H32) -> Res<bool> {
    let me = state.device()?.device_id;
    let Ok(t) = state.team(team) else { return Ok(false) };
    if t.chain.head().active_device(&me).is_some_and(|(m, _)| m.role.is_admin()) { return Ok(false); }
    let gids: Vec<H32> = state.groups.values().filter(|g| g.team_id == *team && g.kind == GroupType::Admin).map(|g| g.group_id).collect();
    for gid in &gids {
        if let Ok(mut g) = crate::mls::load_group(p, gid) {
            g.delete(openmls_traits::OpenMlsProvider::storage(p)).map_err(|x| e(Code::Mls, format!("{x:?}")))?;
        }
        state.groups.remove(&hex(gid));
    }
    let had = !state.team(team)?.invites.is_empty();
    state.team_mut(team)?.invites.clear();
    Ok(!gids.is_empty() || had)
}
