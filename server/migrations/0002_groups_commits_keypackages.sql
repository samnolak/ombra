-- MLS group tracking on the server (§3.1, §6.1 /commit, N1, N2). Forward-only.

-- Opaque ombra-core PublicGroup state (pgroup.init / pgroup.commit); epoch/leaves above it are derived from it.
ALTER TABLE groups ADD COLUMN group_state bytea;

-- Queue rows now also carry handshake traffic: a commit for existing leaves, a Welcome for each added device.
ALTER TABLE delivery ADD COLUMN kind text NOT NULL DEFAULT 'op' CHECK (kind IN ('op', 'commit', 'welcome'));

-- §6.1 KeyPackages (SD-11, N1). The KP itself is verified by ombra-core (credential = deviceId, signature, lifetime).
CREATE TABLE key_packages (
    kp_ref      bytea PRIMARY KEY,
    device_id   uuid NOT NULL,
    key_package bytea NOT NULL,
    last_resort boolean NOT NULL,
    not_after   timestamptz NOT NULL,
    uploaded_at timestamptz NOT NULL,
    issued_at   timestamptz
);
CREATE INDEX key_packages_stock ON key_packages (device_id) WHERE issued_at IS NULL;

-- N1: who got which KP; a KP counts as used only when a commit with its Add was accepted (consumed_at).
CREATE TABLE kp_issuance (
    kp_ref              bytea NOT NULL,
    requester_device_id uuid NOT NULL,
    target_device_id    uuid NOT NULL,
    issued_at           timestamptz NOT NULL,
    consumed_at         timestamptz,
    PRIMARY KEY (kp_ref, requester_device_id)
);
CREATE INDEX kp_issuance_pair ON kp_issuance (requester_device_id, target_device_id, consumed_at);
CREATE INDEX kp_issuance_requester ON kp_issuance (requester_device_id, issued_at);
