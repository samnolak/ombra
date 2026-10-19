-- R2-05: the team's admin group (owner/admin devices only) carries invite secrets k_adm. Forward-only.
ALTER TABLE groups DROP CONSTRAINT groups_kind_check;
ALTER TABLE groups ADD CONSTRAINT groups_kind_check CHECK (kind IN ('team', 'project', 'pair', 'personal', 'admin'));

-- N1 per group: an admin adding the same device to several groups the same day (team, project, admin groups;
-- a paired device joining existing groups) asks per group; the 24 h limit counts consumptions in that group.
ALTER TABLE kp_issuance ADD COLUMN consumed_group bytea;
