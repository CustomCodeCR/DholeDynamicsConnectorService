-- Phase 06: durable idempotency ledger; no OAuth secrets or raw personal record payloads.
CREATE TABLE IF NOT EXISTS dynamics.write_operations (
    id uuid PRIMARY KEY,
    connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
    issuer text NOT NULL,
    oauth_client_id varchar(128) NOT NULL,
    idempotency_key varchar(128) NOT NULL,
    request_sha256 char(64) NOT NULL,
    entity_name varchar(128) NOT NULL,
    operation varchar(16) NOT NULL CHECK (operation IN ('create','update','delete','upsert','batch')),
    status varchar(24) NOT NULL CHECK(status IN
        ('reserved','succeeded','rejected','ambiguous','reconciled')),
    http_status integer,
    response jsonb,
    record_id uuid,
    batch_count integer,
    started_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_write_client_idempotency UNIQUE
        (connection_id, issuer, oauth_client_id, idempotency_key),
    CONSTRAINT chk_write_hash CHECK (request_sha256 ~ '^[A-F0-9]{64}$')
);
CREATE INDEX IF NOT EXISTS ix_write_ambiguous
 ON dynamics.write_operations(connection_id, started_at)
 WHERE status IN ('reserved','ambiguous');
CREATE TABLE IF NOT EXISTS dynamics.delete_policies (
    connection_id uuid NOT NULL,
    entity_name varchar(128) NOT NULL,
    enabled boolean NOT NULL DEFAULT false,
    min_age_days integer NOT NULL DEFAULT 0 CHECK (min_age_days BETWEEN 0 AND 36500),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(connection_id,entity_name),
    FOREIGN KEY(connection_id,entity_name)
      REFERENCES dynamics.metadata_allowed_entities(connection_id, logical_name) ON DELETE CASCADE
);

-- Upgrade the previous read/create/update/delete grant whitelist for Phase 06 upsert.
ALTER TABLE dynamics.api_grants DROP CONSTRAINT IF EXISTS chk_grant_operation;
ALTER TABLE dynamics.api_grants ADD CONSTRAINT chk_grant_operation
 CHECK (operation IN ('create','read','update','delete','upsert'));
