-- Ombra team server: initial schema (SERVER_DESIGN §1, §3.0, §3.3, §4.1, §4.2, §5, §6.1).
-- Forward-only: never edit an applied migration; add a new file instead.
-- Invariants worth reviewing are written next to the tables that hold them.

CREATE TABLE server_state (
    key   text PRIMARY KEY,
    value text NOT NULL
);
-- keys: server_id (uuid, from /etc/ombra), cursor_epoch (uuid, rotated on restore → sync cursors 410),
--       token_key_id (hex id of the token HMAC key in use), restore_in_progress ('1' while a restore runs).

-- Global change counter for delta sync of roster/blocks/pending rotations.
CREATE SEQUENCE change_seq;

-- §2.4: one-time bootstrap code (only its SHA-256), printed by install.sh together with the SPKI pin.
CREATE TABLE bootstrap_codes (
    code_hash      bytea PRIMARY KEY CHECK (length(code_hash) = 32),
    spki_pin       text,
    created_at     timestamptz NOT NULL,
    expires_at     timestamptz NOT NULL,
    used_at        timestamptz,
    used_by_device uuid
);

-- Members/devices known to the server. Rows are never deleted: memberId and deviceId are unique forever
-- (R5-06, N2b), so a block can never be "inherited" by re-registering an id.
CREATE TABLE members (
    member_id  uuid PRIMARY KEY,
    created_at timestamptz NOT NULL
);

CREATE TABLE devices (
    device_id         uuid PRIMARY KEY,
    member_id         uuid NOT NULL REFERENCES members (member_id),
    product           text NOT NULL CHECK (product IN ('gui', 'browser')),
    identity_key      bytea NOT NULL CHECK (length(identity_key) = 32),
    mls_signature_key bytea NOT NULL CHECK (length(mls_signature_key) = 32),
    created_at        timestamptz NOT NULL,
    revoked_at        timestamptz
);
CREATE INDEX devices_member ON devices (member_id);

-- ServerOwner: right to create teams on this instance; grants no access to content (§2.4).
CREATE TABLE server_owners (
    member_id  uuid PRIMARY KEY REFERENCES members (member_id),
    granted_at timestamptz NOT NULL
);

-- Teams and the server-side projection of the signed roster (§4.1). The roster chain itself is verified by
-- ombra-core; these tables only mirror the verified head for ACL decisions.
CREATE TABLE teams (
    team_id                bytea PRIMARY KEY CHECK (length(team_id) = 32),
    created_at             timestamptz NOT NULL,
    access_revision        bigint NOT NULL DEFAULT 0,
    roster_version         bigint NOT NULL,
    roster_hash            bytea NOT NULL CHECK (length(roster_hash) = 32),
    chain_state            bytea,
    quarantine             boolean NOT NULL DEFAULT false,
    quarantine_released_by text CHECK (quarantine_released_by IN ('owner', 'operator')),
    frozen_reason          text
);

CREATE TABLE roster_versions (
    team_id       bytea NOT NULL REFERENCES teams (team_id),
    version       bigint NOT NULL,
    hash          bytea NOT NULL CHECK (length(hash) = 32),
    prev_hash     bytea,
    roster_update bytea NOT NULL,
    accepted_at   timestamptz NOT NULL,
    change_seq    bigint NOT NULL DEFAULT nextval('change_seq'),
    PRIMARY KEY (team_id, version)
);
CREATE INDEX roster_versions_change ON roster_versions (change_seq);

CREATE TABLE roster_members (
    team_id            bytea NOT NULL REFERENCES teams (team_id),
    member_id          uuid NOT NULL,
    role               text NOT NULL CHECK (role IN ('owner', 'admin', 'member')),
    added_in_version   bigint NOT NULL,
    removed_in_version bigint,
    PRIMARY KEY (team_id, member_id)
);

CREATE TABLE roster_devices (
    team_id            bytea NOT NULL REFERENCES teams (team_id),
    device_id          uuid NOT NULL,
    member_id          uuid NOT NULL,
    identity_key_hash  bytea NOT NULL CHECK (length(identity_key_hash) = 32),
    mls_key_hash       bytea NOT NULL CHECK (length(mls_key_hash) = 32),
    cert_hash          bytea NOT NULL CHECK (length(cert_hash) = 32),
    added_in_version   bigint NOT NULL,
    revoked_in_version bigint,
    PRIMARY KEY (team_id, device_id),
    FOREIGN KEY (team_id, member_id) REFERENCES roster_members (team_id, member_id)
);
CREATE INDEX roster_devices_device ON roster_devices (device_id);

CREATE TABLE roster_access (
    team_id    bytea NOT NULL REFERENCES teams (team_id),
    project_id uuid NOT NULL,
    member_id  uuid NOT NULL,
    level      text NOT NULL CHECK (level IN ('viewer', 'contributor')),
    PRIMARY KEY (team_id, project_id, member_id)
);

-- §4.2 reducing ACL. A block is never lifted by the server or the CLI (§5.2 п.5).
-- No FK to teams/roster on purpose: replaying revocations.log after restore must record blocks for teams/members
-- that the restored DB does not know yet (created after the backup); they take effect once the chain is re-uploaded.
CREATE TABLE blocks (
    block_id   bigserial PRIMARY KEY,
    team_id    bytea NOT NULL CHECK (length(team_id) = 32),
    member_id  uuid,
    device_id  uuid,
    by_actor   text NOT NULL CHECK (by_actor IN ('operator', 'admin')),
    at         timestamptz NOT NULL,
    reason     text,
    audit_id   uuid NOT NULL,
    change_seq bigint NOT NULL DEFAULT nextval('change_seq'),
    CHECK ((member_id IS NULL) <> (device_id IS NULL))
);
CREATE UNIQUE INDEX blocks_member ON blocks (team_id, member_id) WHERE member_id IS NOT NULL;
CREATE UNIQUE INDEX blocks_device ON blocks (team_id, device_id) WHERE device_id IS NOT NULL;
CREATE INDEX blocks_change ON blocks (change_seq);

-- Server right = roster allows AND no block (§4.2). Single definition used by every ACL query.
CREATE VIEW team_device_access AS
SELECT d.team_id, d.device_id, d.member_id, m.role
FROM roster_devices d
JOIN roster_members m ON m.team_id = d.team_id AND m.member_id = d.member_id
JOIN devices dv ON dv.device_id = d.device_id
WHERE d.revoked_in_version IS NULL
  AND m.removed_in_version IS NULL
  AND dv.revoked_at IS NULL
  -- the key the server authenticates (hello/envelopes) must be the one the signed roster endorses
  AND sha256(dv.identity_key) = d.identity_key_hash
  AND NOT EXISTS (SELECT 1 FROM blocks b
                  WHERE b.team_id = d.team_id
                    AND (b.member_id = d.member_id OR b.device_id = d.device_id));

CREATE TABLE audit (
    audit_id uuid PRIMARY KEY,
    at       timestamptz NOT NULL,
    actor    text NOT NULL,
    action   text NOT NULL,
    team_id  bytea,
    subject  text,
    details  jsonb NOT NULL DEFAULT '{}'::jsonb
);
CREATE INDEX audit_at ON audit (at);

-- §5.2 п.2
CREATE TABLE pending_rotations (
    rotation_id       uuid PRIMARY KEY,
    team_id           bytea NOT NULL REFERENCES teams (team_id),
    group_ids         bytea[] NOT NULL,
    blocked_member_id uuid,
    blocked_device_id uuid,
    audit_id          uuid NOT NULL REFERENCES audit (audit_id),
    created_at        timestamptz NOT NULL,
    resolved_at       timestamptz,
    change_seq        bigint NOT NULL DEFAULT nextval('change_seq')
);
CREATE INDEX pending_rotations_change ON pending_rotations (change_seq);

-- MLS groups as seen by the server (§3.1). epoch/leaves are maintained by the commit path (ombra-core public check).
-- Lock order: the revoke transaction locks all group rows of a team ordered by group_id; /ops locks one row.
CREATE TABLE groups (
    group_id        bytea PRIMARY KEY CHECK (length(group_id) = 32),
    team_id         bytea NOT NULL REFERENCES teams (team_id),
    kind            text NOT NULL CHECK (kind IN ('team', 'project', 'pair', 'personal')),
    project_id      uuid,
    member_a        uuid,
    member_b        uuid,
    owner_member_id uuid,
    generation      integer NOT NULL DEFAULT 0,
    epoch           bigint NOT NULL,
    closed_at       timestamptz,
    created_at      timestamptz NOT NULL
);
CREATE INDEX groups_team ON groups (team_id);

CREATE TABLE group_leaves (
    group_id  bytea NOT NULL REFERENCES groups (group_id) ON DELETE CASCADE,
    device_id uuid NOT NULL,
    PRIMARY KEY (group_id, device_id)
);
CREATE INDEX group_leaves_device ON group_leaves (device_id);

-- §3.0 delivery queue (D-7: not an archive, excluded from backups).
-- Invariant (R5-06): pending_device_ids never contains a device that is blocked/revoked at commit time of the row's
-- insert or of any later revoke transaction. Rows whose pending list became empty are deleted in the same transaction
-- (ack, revoke), so no committed row has an empty list.
CREATE TABLE delivery (
    group_id           bytea NOT NULL REFERENCES groups (group_id),
    sender_device_id   uuid NOT NULL,
    op_id              uuid NOT NULL,
    seq                bigserial NOT NULL UNIQUE,
    team_id            bytea NOT NULL REFERENCES teams (team_id),
    sender_member_id   uuid NOT NULL,
    epoch              bigint NOT NULL,
    ciphertext         bytea NOT NULL,
    size               integer NOT NULL,
    pending_device_ids uuid[] NOT NULL,
    created_at         timestamptz NOT NULL,
    expires_at         timestamptz NOT NULL,
    PRIMARY KEY (group_id, sender_device_id, op_id)
);
CREATE INDEX delivery_pending ON delivery USING gin (pending_device_ids);
CREATE INDEX delivery_sender ON delivery (team_id, sender_member_id, seq);
CREATE INDEX delivery_expires ON delivery (expires_at);

-- Envelope dedupe by (deviceId, opId) with the stored result (§6.1). Short-lived: envelopes outside the ts window
-- are rejected anyway.
CREATE TABLE envelope_results (
    device_id    uuid NOT NULL,
    op_id        uuid NOT NULL,
    request_hash bytea NOT NULL,
    status       integer NOT NULL,
    response     jsonb NOT NULL,
    created_at   timestamptz NOT NULL,
    PRIMARY KEY (device_id, op_id)
);
CREATE INDEX envelope_results_created ON envelope_results (created_at);

-- §3.3 one-shot transfers (excluded from backups). Ciphertext chunks live on disk under <data>/transfers/<id>/.
-- At most one active transfer per target device (unique index); rows are deleted after ack/TTL/revoke.
CREATE TABLE transfers (
    transfer_id      uuid PRIMARY KEY,
    team_id          bytea NOT NULL REFERENCES teams (team_id),
    sender_device_id uuid NOT NULL,
    sender_member_id uuid NOT NULL,
    target_device_id uuid NOT NULL,
    target_member_id uuid NOT NULL,
    size             bigint NOT NULL,
    sha256           bytea NOT NULL CHECK (length(sha256) = 32),
    total_chunks     integer NOT NULL,
    created_at       timestamptz NOT NULL,
    expires_at       timestamptz NOT NULL
);
CREATE UNIQUE INDEX transfers_one_per_target ON transfers (target_device_id);
CREATE INDEX transfers_sender ON transfers (sender_member_id, created_at);

CREATE TABLE transfer_chunks (
    transfer_id uuid NOT NULL REFERENCES transfers (transfer_id) ON DELETE CASCADE,
    idx         integer NOT NULL,
    size        integer NOT NULL,
    is_final    boolean NOT NULL,
    PRIMARY KEY (transfer_id, idx)
);

-- Transfer creation log for the per-sender hourly rate (kept after the transfer row is gone).
CREATE TABLE transfer_rate (
    sender_member_id uuid NOT NULL,
    at               timestamptz NOT NULL
);
CREATE INDEX transfer_rate_sender ON transfer_rate (sender_member_id, at);

-- §3.3 attachment metadata; ciphertext lives at <data>/blobs/<id>. In backups (ciphertext until DELETE/TTL).
CREATE TABLE blobs (
    blob_id            uuid PRIMARY KEY,
    team_id            bytea NOT NULL REFERENCES teams (team_id),
    group_id           bytea NOT NULL REFERENCES groups (group_id),
    uploader_member_id uuid NOT NULL,
    uploader_device_id uuid NOT NULL,
    size               bigint NOT NULL,
    sha256             bytea NOT NULL CHECK (length(sha256) = 32),
    created_at         timestamptz NOT NULL,
    last_access_at     timestamptz NOT NULL
);
CREATE INDEX blobs_team ON blobs (team_id);
CREATE INDEX blobs_last_access ON blobs (last_access_at);

-- §2.2 sessions. Tokens are stored as HMAC(token key, token); rotating the key or truncating the table kills all.
CREATE TABLE sessions (
    session_id         uuid PRIMARY KEY,
    device_id          uuid NOT NULL REFERENCES devices (device_id),
    access_hash        bytea NOT NULL UNIQUE,
    access_expires_at  timestamptz NOT NULL,
    refresh_hash       bytea NOT NULL UNIQUE,
    refresh_expires_at timestamptz NOT NULL,
    created_at         timestamptz NOT NULL
);
CREATE INDEX sessions_device ON sessions (device_id);

CREATE TABLE hello_challenges (
    nonce      bytea PRIMARY KEY CHECK (length(nonce) = 32),
    device_id  uuid NOT NULL,
    expires_at timestamptz NOT NULL
);

-- §3.4 invites: the server stores only verifier = SHA-256(k_srv) and routing fields.
CREATE TABLE invites (
    invite_id           uuid PRIMARY KEY,
    team_id             bytea NOT NULL REFERENCES teams (team_id),
    signed_invite       bytea NOT NULL,
    roster_head_version bigint NOT NULL,
    roster_head_hash    bytea NOT NULL CHECK (length(roster_head_hash) = 32),
    role                text NOT NULL CHECK (role IN ('member', 'admin')),
    expires_at          timestamptz NOT NULL,
    max_uses            integer NOT NULL,
    uses                integer NOT NULL DEFAULT 0,
    verifier            bytea NOT NULL CHECK (length(verifier) = 32),
    created_by_device   uuid NOT NULL,
    created_at          timestamptz NOT NULL
);

CREATE TABLE join_requests (
    request_id     uuid PRIMARY KEY,
    invite_id      uuid NOT NULL REFERENCES invites (invite_id),
    team_id        bytea NOT NULL REFERENCES teams (team_id),
    member_id      uuid NOT NULL,
    join_envelope  bytea NOT NULL,
    join_signature bytea NOT NULL,
    created_at     timestamptz NOT NULL,
    resolved_at    timestamptz,
    change_seq     bigint NOT NULL DEFAULT nextval('change_seq')
);
CREATE INDEX join_requests_team ON join_requests (team_id) WHERE resolved_at IS NULL;

-- deviceId / memberId registry: an id is consumed forever on first use (bootstrap, JoinRequest, pairing).
CREATE TABLE used_ids (
    id     uuid PRIMARY KEY,
    kind   text NOT NULL CHECK (kind IN ('device', 'member')),
    source text NOT NULL,
    at     timestamptz NOT NULL
);
