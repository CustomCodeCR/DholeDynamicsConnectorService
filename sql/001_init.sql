-- Idempotent bootstrap for dedicated Dhole Dynamics PostgreSQL database.
-- Store only application metadata and ENCRYPTED client credentials.
CREATE SCHEMA IF NOT EXISTS dynamics;

CREATE TABLE IF NOT EXISTS dynamics.connections (
    id uuid PRIMARY KEY,
    code varchar(64) NOT NULL UNIQUE,
    dataverse_url text NOT NULL,
    tenant_id uuid NOT NULL,
    client_id uuid NOT NULL,
    default_currency_id uuid NULL,
    secret_nonce bytea NOT NULL,
    secret_ciphertext bytea NOT NULL,
    secret_tag bytea NOT NULL,
    secret_key_version integer NOT NULL DEFAULT 1,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT chk_dynamics_url CHECK (dataverse_url LIKE 'https://%'),
    CONSTRAINT chk_secret_nonce_len CHECK (octet_length(secret_nonce) = 12),
    CONSTRAINT chk_secret_tag_len CHECK (octet_length(secret_tag) = 16),
    CONSTRAINT chk_secret_cipher_len CHECK (octet_length(secret_ciphertext) > 0)
);

CREATE TABLE IF NOT EXISTS dynamics.quote_links (
    id uuid PRIMARY KEY,
    connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE RESTRICT,
    dhole_rate_id uuid NOT NULL,
    dynamics_quote_id uuid NULL,
    status varchar(20) NOT NULL DEFAULT 'PENDING',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_dynamics_quote_per_rate UNIQUE(connection_id, dhole_rate_id),
    CONSTRAINT chk_quote_link_status CHECK (status IN ('PENDING','CREATED','UNKNOWN'))
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_dynamics_quote_id
    ON dynamics.quote_links(connection_id, dynamics_quote_id)
    WHERE dynamics_quote_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS dynamics.external_references (
    id uuid PRIMARY KEY,
    connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE RESTRICT,
    dhole_entity_type varchar(80) NOT NULL,
    dhole_entity_id uuid NOT NULL,
    dataverse_entity_set varchar(100) NOT NULL,
    dataverse_entity_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(connection_id, dhole_entity_type, dhole_entity_id, dataverse_entity_set)
);

CREATE TABLE IF NOT EXISTS dynamics.audit_events (
    id uuid PRIMARY KEY,
    connection_id uuid NULL REFERENCES dynamics.connections(id) ON DELETE SET NULL,
    quote_link_id uuid NULL REFERENCES dynamics.quote_links(id) ON DELETE SET NULL,
    actor_id text NOT NULL,
    action varchar(100) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_dynamics_quote_created
    ON dynamics.quote_links(created_at DESC);
CREATE INDEX IF NOT EXISTS ix_dynamics_audit_created
    ON dynamics.audit_events(created_at DESC);
