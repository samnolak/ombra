-- §2.3 pairing / DeviceCertificate / DeviceRevocation, §4.1 heartbeat, §4.3 ownership offers, §5.1 п.6
-- RemovalNotice storage. Forward-only.

-- Devices registered by a DeviceCertificate (§2.3 п.3) remember the issuer and the certificate; the server-side
-- cascade (§2.3 п.5) follows issuer_device_id for devices registered at or after compromisedSince.
ALTER TABLE devices ADD COLUMN issuer_device_id uuid;
ALTER TABLE devices ADD COLUMN certificate bytea;
-- Signed (by the issuer) list of teams the paired device belongs to: the new device trusts only these teamIds.
ALTER TABLE devices ADD COLUMN grant_envelope bytea;
ALTER TABLE devices ADD COLUMN grant_signature bytea;
CREATE INDEX devices_issuer ON devices (issuer_device_id) WHERE issuer_device_id IS NOT NULL;

-- §2.3 SAS channel: the server only relays. The existing device commits first; the new device answers in clear;
-- only then the existing device reveals. Short code = rendezvous (not a secret: a wrong party only yields other words).
CREATE TABLE pairing_sessions (
    code                 text PRIMARY KEY,
    member_id            uuid NOT NULL REFERENCES members (member_id),
    existing_device_id   uuid NOT NULL REFERENCES devices (device_id),
    existing_commitment  bytea NOT NULL CHECK (length(existing_commitment) = 32),
    new_device_id        uuid,
    new_identity_key     bytea CHECK (length(new_identity_key) = 32),
    new_mls_key          bytea CHECK (length(new_mls_key) = 32),
    new_nonce            bytea CHECK (length(new_nonce) = 32),
    new_product          text CHECK (new_product IN ('gui', 'browser')),
    existing_nonce       bytea CHECK (length(existing_nonce) = 32),
    certified_at         timestamptz,
    created_at           timestamptz NOT NULL,
    expires_at           timestamptz NOT NULL
);
CREATE INDEX pairing_sessions_expires ON pairing_sessions (expires_at);

-- §4.1 heartbeat (SD-06): the latest signed heartbeat per owner/admin device; clients verify epochProofs.
CREATE TABLE heartbeats (
    team_id        bytea NOT NULL REFERENCES teams (team_id),
    device_id      uuid NOT NULL,
    roster_version bigint NOT NULL,
    time           timestamptz NOT NULL,
    heartbeat      bytea NOT NULL,
    received_at    timestamptz NOT NULL,
    PRIMARY KEY (team_id, device_id)
);

-- §4.3: the owner's signed OwnershipOffer, one open offer per team; accepted by a RosterUpdate with both signatures.
CREATE TABLE ownership_offers (
    offer_id       uuid PRIMARY KEY,
    team_id        bytea NOT NULL REFERENCES teams (team_id),
    from_member    uuid NOT NULL,
    to_member      uuid NOT NULL,
    roster_version bigint NOT NULL,
    offer          jsonb NOT NULL,
    offer_sig      jsonb NOT NULL,
    expires_at     timestamptz NOT NULL,
    created_at     timestamptz NOT NULL,
    closed_at      timestamptz,
    accepted       boolean
);
CREATE UNIQUE INDEX ownership_offers_open ON ownership_offers (team_id) WHERE closed_at IS NULL;

-- §5.1 п.6: admin-signed RemovalNotice, served to the removed member's devices with every 403 removed.
CREATE TABLE removal_notices (
    team_id        bytea NOT NULL REFERENCES teams (team_id),
    member_id      uuid NOT NULL,
    roster_version bigint NOT NULL,
    notice         bytea NOT NULL,
    created_at     timestamptz NOT NULL,
    PRIMARY KEY (team_id, member_id)
);
