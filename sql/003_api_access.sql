-- Phase 03: independent M2M consumer registry; Dataverse secrets stay in connections.
-- No OAuth tokens or shared API-client credentials are stored in this schema.
CREATE TABLE IF NOT EXISTS dynamics.api_clients (
  id uuid PRIMARY KEY,
  issuer text NOT NULL,
  oauth_client_id varchar(128) NOT NULL,
  display_name varchar(120) NOT NULL,
  enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT uq_api_client_issuer_subject UNIQUE (issuer, oauth_client_id),
  CONSTRAINT chk_api_client_issuer_https CHECK (issuer LIKE 'https://%')
);
CREATE TABLE IF NOT EXISTS dynamics.api_grants (
  id uuid PRIMARY KEY,
  api_client_id uuid NOT NULL REFERENCES dynamics.api_clients(id) ON DELETE CASCADE,
  connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
  entity_set varchar(100) NOT NULL,
  operation varchar(20) NOT NULL,
  allowed_fields text[] NOT NULL,
  max_requests_per_minute integer NOT NULL CHECK (max_requests_per_minute BETWEEN 1 AND 600),
  enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (api_client_id, connection_id, entity_set, operation),
  CONSTRAINT chk_grant_operation CHECK (operation IN ('create','read','update','delete')),
  CONSTRAINT chk_grant_entity CHECK (entity_set ~ '^[a-z][a-z0-9_]{0,99}$'),
  CONSTRAINT chk_grant_fields_nonempty CHECK (cardinality(allowed_fields) > 0)
);
CREATE TABLE IF NOT EXISTS dynamics.api_quota_windows (
  grant_id uuid NOT NULL REFERENCES dynamics.api_grants(id) ON DELETE CASCADE,
  window_minute bigint NOT NULL,
  request_count integer NOT NULL CHECK(request_count >= 1),
  PRIMARY KEY (grant_id, window_minute)
);
CREATE INDEX IF NOT EXISTS ix_grants_connection ON dynamics.api_grants(connection_id, entity_set, operation);
CREATE INDEX IF NOT EXISTS ix_api_quota_window ON dynamics.api_quota_windows(window_minute);
