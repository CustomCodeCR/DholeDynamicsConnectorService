-- Dedicated immutable-version API credentials for approved Dataverse CRUD templates.
-- Keys do not grant OAuth access, wildcard field access or write approvals.
CREATE TABLE dynamics.template_api_keys (
  id uuid PRIMARY KEY,
  connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
  template_id uuid NOT NULL REFERENCES dynamics.integration_templates(id) ON DELETE CASCADE,
  template_version integer NOT NULL,
  operation varchar(12) NOT NULL CHECK (operation IN ('create','update','upsert','delete')),
  entity_name varchar(128) NOT NULL,
  allowed_fields text[] NOT NULL CHECK (cardinality(allowed_fields)>0),
  key_fields text[] NOT NULL DEFAULT '{}',
  client_id uuid NOT NULL REFERENCES dynamics.api_clients(id),
  key_sha256 bytea NOT NULL UNIQUE CHECK(octet_length(key_sha256)=32),
  label varchar(120) NOT NULL,
  enabled boolean NOT NULL DEFAULT true,
  expires_at timestamptz NOT NULL,
  max_requests_per_minute integer NOT NULL CHECK(max_requests_per_minute BETWEEN 1 AND 120),
  created_by varchar(128) NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  last_used_at timestamptz NULL,
  FOREIGN KEY(template_id,template_version)
    REFERENCES dynamics.template_versions(template_id,version)
);
CREATE INDEX ix_template_api_keys_connection
 ON dynamics.template_api_keys(connection_id,created_at DESC);
CREATE TABLE dynamics.template_api_key_quota (
  key_id uuid NOT NULL REFERENCES dynamics.template_api_keys(id) ON DELETE CASCADE,
  minute bigint NOT NULL,
  requests integer NOT NULL CHECK(requests>0),
  PRIMARY KEY(key_id,minute)
);
