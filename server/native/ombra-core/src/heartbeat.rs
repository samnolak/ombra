//! Heartbeat (§4.1, SD-06, R2-02). Signed bytes (canonical, fixed layout):
//! `"ombra/v1/heartbeat" ‖ 0x00 ‖ teamId[32] ‖ rosterVersion u64 BE ‖ rosterHash[32] ‖ u16 n ‖
//!  n × (u16 len ‖ groupId ‖ epoch u64 BE ‖ epochProof[32]) ‖ time u64 BE ‖ signerDeviceId[16]`,
//! groups strictly ascending by groupId. Wire form = signed bytes ‖ Ed25519 signature[64].
//! `epochProof = MLS-Exporter("ombra/v1/heartbeat", teamId ‖ time u64 BE, 32)` of that epoch.

use crate::util::{Code, Id16, Res, H32};
use crate::ensure;

pub const HB_DOMAIN: &[u8] = b"ombra/v1/heartbeat";
pub const HB_EXPORTER_LABEL: &str = "ombra/v1/heartbeat";

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HbGroup {
    pub group_id: Vec<u8>,
    pub epoch: u64,
    pub epoch_proof: H32,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Heartbeat {
    pub team_id: H32,
    pub roster_version: u64,
    pub roster_hash: H32,
    pub groups: Vec<HbGroup>,
    pub time: u64,
    pub signer_device_id: Id16,
}

pub fn exporter_context(team_id: &H32, time: u64) -> Vec<u8> {
    let mut c = team_id.to_vec();
    c.extend_from_slice(&time.to_be_bytes());
    c
}

impl Heartbeat {
    pub fn signed_bytes(&self) -> Res<Vec<u8>> {
        let mut gs = self.groups.clone();
        gs.sort_by(|a, b| a.group_id.cmp(&b.group_id));
        ensure!(gs.windows(2).all(|w| w[0].group_id != w[1].group_id), Code::InvalidInput, "duplicate groupId in heartbeat");
        ensure!(gs.len() <= u16::MAX as usize, Code::InvalidInput, "too many groups");
        let mut m = HB_DOMAIN.to_vec();
        m.push(0);
        m.extend_from_slice(&self.team_id);
        m.extend_from_slice(&self.roster_version.to_be_bytes());
        m.extend_from_slice(&self.roster_hash);
        m.extend_from_slice(&(gs.len() as u16).to_be_bytes());
        for g in &gs {
            ensure!(g.group_id.len() <= u16::MAX as usize, Code::InvalidInput, "groupId too long");
            m.extend_from_slice(&(g.group_id.len() as u16).to_be_bytes());
            m.extend_from_slice(&g.group_id);
            m.extend_from_slice(&g.epoch.to_be_bytes());
            m.extend_from_slice(&g.epoch_proof);
        }
        m.extend_from_slice(&self.time.to_be_bytes());
        m.extend_from_slice(&self.signer_device_id);
        Ok(m)
    }

    /// Parse wire bytes into (heartbeat, signed bytes, signature). Rejects any layout that is not
    /// exactly the canonical one (unsorted groups, trailing bytes).
    pub fn parse(wire: &[u8]) -> Res<(Heartbeat, Vec<u8>, [u8; 64])> {
        ensure!(wire.len() > 64, Code::InvalidInput, "heartbeat too short");
        let (body, sig) = wire.split_at(wire.len() - 64);
        let mut r = Reader { b: body, p: 0 };
        ensure!(r.take(HB_DOMAIN.len() + 1)? == [HB_DOMAIN, &[0u8][..]].concat().as_slice(), Code::InvalidInput, "bad heartbeat domain");
        let team_id: H32 = r.take(32)?.try_into().unwrap();
        let roster_version = r.u64()?;
        let roster_hash: H32 = r.take(32)?.try_into().unwrap();
        let n = r.u16()? as usize;
        let mut groups = Vec::with_capacity(n.min(256));
        for _ in 0..n {
            let l = r.u16()? as usize;
            let group_id = r.take(l)?.to_vec();
            let epoch = r.u64()?;
            let epoch_proof: H32 = r.take(32)?.try_into().unwrap();
            groups.push(HbGroup { group_id, epoch, epoch_proof });
        }
        ensure!(groups.windows(2).all(|w| w[0].group_id < w[1].group_id), Code::InvalidInput, "heartbeat groups not strictly ascending");
        let time = r.u64()?;
        let signer_device_id: Id16 = r.take(16)?.try_into().unwrap();
        ensure!(r.p == body.len(), Code::InvalidInput, "trailing bytes in heartbeat");
        Ok((Heartbeat { team_id, roster_version, roster_hash, groups, time, signer_device_id }, body.to_vec(), sig.try_into().unwrap()))
    }
}

pub(crate) struct Reader<'a> { pub b: &'a [u8], pub p: usize }

impl<'a> Reader<'a> {
    pub fn take(&mut self, n: usize) -> Res<&'a [u8]> {
        ensure!(self.p + n <= self.b.len(), Code::InvalidInput, "truncated");
        let s = &self.b[self.p..self.p + n];
        self.p += n;
        Ok(s)
    }
    pub fn u16(&mut self) -> Res<u16> { Ok(u16::from_be_bytes(self.take(2)?.try_into().unwrap())) }
    pub fn u64(&mut self) -> Res<u64> { Ok(u64::from_be_bytes(self.take(8)?.try_into().unwrap())) }
}
