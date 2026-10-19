-- R2-08 / §1 карантин п.3: evidence of a roster equivocation (both signed versions with one number) and the
-- owner's signed decision that unfroze the team. Forward-only.
CREATE TABLE roster_equivocations (
    team_id      bytea NOT NULL REFERENCES teams (team_id),
    version      bigint NOT NULL,
    known        bytea NOT NULL,
    conflicting  bytea NOT NULL,
    detected_at  timestamptz NOT NULL,
    resolution   bytea,
    switched     boolean,
    resolved_at  timestamptz,
    resolved_by  text,
    PRIMARY KEY (team_id, detected_at)
);
-- At most one open (unresolved) equivocation per team: the team is frozen while it exists.
CREATE UNIQUE INDEX roster_equivocations_open ON roster_equivocations (team_id) WHERE resolved_at IS NULL;

-- Pre-auth quota (implementation review): outstanding hello challenges are counted per device.
CREATE INDEX hello_challenges_device ON hello_challenges (device_id, expires_at);

-- Roster version of each group's current epoch (from the public verification): after an owner resolution switches
-- the roster branch, groups on a replaced version (>= the resolved one) are closed and recreated as a new generation.
ALTER TABLE groups ADD COLUMN roster_version bigint;
