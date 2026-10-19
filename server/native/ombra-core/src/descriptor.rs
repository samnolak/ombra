//! Group descriptor (§3.1, R5-01, N2): immutable GroupContext extension `ombra_group_descriptor`
//! (0xF0A2, in RequiredCapabilities). Canonical CBOR
//! `{type: "team"|"project"|"pair"|"personal", teamId, projectId?, members?: [a, b] (pair,
//! strictly ascending), owner? (personal), generation}`; `groupId = SHA-256(canonical)`.
//! Admissible leaves per type are computed in `mls::admitted` from the verified roster.

use serde::{Deserialize, Serialize};

use crate::cbor::{self, b, t, Fields, MapB, Value};
use crate::ensure;
use crate::util::{self, hexopt, hexser, hexvec, Code, Id16, Res, H32};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
/// `Admin` (R2-05): the team's owner/admin devices only — the channel for invite secrets (`k_adm`).
pub enum GroupType { Team, Project, Pair, Personal, Admin }

impl GroupType {
    pub fn as_str(self) -> &'static str {
        match self { GroupType::Team => "team", GroupType::Project => "project", GroupType::Pair => "pair", GroupType::Personal => "personal", GroupType::Admin => "admin" }
    }
    /// Groups an admin manages (create, Add, GroupContextExtensions): team, project, admin.
    pub fn admin_scope(self) -> bool { matches!(self, GroupType::Team | GroupType::Project | GroupType::Admin) }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Descriptor {
    #[serde(rename = "type")]
    pub kind: GroupType,
    #[serde(with = "hexser")] pub team_id: H32,
    #[serde(default, with = "hexopt", skip_serializing_if = "Option::is_none")]
    pub project_id: Option<Id16>,
    #[serde(default, with = "hexvec", skip_serializing_if = "Vec::is_empty")]
    pub members: Vec<Id16>,
    #[serde(default, with = "hexopt", skip_serializing_if = "Option::is_none")]
    pub owner: Option<Id16>,
    pub generation: u32,
}

impl Descriptor {
    pub fn validate(&self) -> Res<()> {
        match self.kind {
            GroupType::Team | GroupType::Admin => ensure!(self.project_id.is_none() && self.members.is_empty() && self.owner.is_none(), Code::InvalidInput, "team/admin descriptor has extra fields"),
            GroupType::Project => ensure!(self.project_id.is_some() && self.members.is_empty() && self.owner.is_none(), Code::InvalidInput, "project descriptor needs projectId only"),
            GroupType::Pair => ensure!(self.project_id.is_none() && self.owner.is_none() && self.members.len() == 2 && self.members[0] < self.members[1],
                Code::InvalidInput, "pair descriptor needs two distinct members in ascending order"),
            GroupType::Personal => ensure!(self.project_id.is_none() && self.members.is_empty() && self.owner.is_some(), Code::InvalidInput, "personal descriptor needs owner only"),
        }
        Ok(())
    }

    pub fn to_value(&self) -> Value {
        MapB::new()
            .put("type", t(self.kind.as_str()))
            .put("teamId", b(&self.team_id))
            .put_opt("projectId", self.project_id.map(|p| b(&p)))
            .put_opt("members", (self.kind == GroupType::Pair).then(|| Value::A(self.members.iter().map(|m| b(m)).collect())))
            .put_opt("owner", self.owner.map(|o| b(&o)))
            .put("generation", Value::U(self.generation as u64))
            .build()
    }

    pub fn canonical(&self) -> Vec<u8> { cbor::encode(&self.to_value()) }
    pub fn group_id(&self) -> H32 { util::sha256(&[&self.canonical()]) }

    pub fn parse(bytes: &[u8]) -> Res<Self> {
        let mut f = Fields::new(cbor::decode(bytes)?, "descriptor")?;
        let kind = match f.req("type")?.text()? {
            "team" => GroupType::Team, "project" => GroupType::Project, "pair" => GroupType::Pair, "personal" => GroupType::Personal, "admin" => GroupType::Admin,
            x => return util::err(Code::InvalidInput, format!("unknown group type '{x}'")),
        };
        let d = Descriptor {
            kind,
            team_id: f.req("teamId")?.fixed()?,
            project_id: f.opt("projectId").map(|v| v.fixed()).transpose()?,
            members: match f.opt("members") { None => vec![], Some(v) => v.array()?.iter().map(|x| x.fixed()).collect::<Result<_, _>>()? },
            owner: f.opt("owner").map(|v| v.fixed()).transpose()?,
            generation: u32::try_from(f.req("generation")?.u()?).map_err(|_| util::e(Code::InvalidInput, "generation > u32"))?,
        };
        f.finish()?;
        d.validate()?;
        Ok(d)
    }

    /// For a pair: the other participant.
    pub fn peer_of(&self, me: &Id16) -> Option<Id16> {
        (self.kind == GroupType::Pair && self.members.contains(me)).then(|| if self.members[0] == *me { self.members[1] } else { self.members[0] })
    }

    /// Members named by the descriptor (pair: both; personal: owner; team/project: none).
    pub fn named_members(&self) -> Vec<Id16> {
        match self.kind { GroupType::Pair => self.members.clone(), GroupType::Personal => self.owner.into_iter().collect(), _ => vec![] }
    }
}
