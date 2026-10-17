-- Phase 04: immutable catalog versions and explicit connection-local allowlists.
-- No Dataverse records, personal data or OAuth access tokens are stored here.
CREATE TABLE IF NOT EXISTS dynamics.metadata_snapshots (
 id uuid PRIMARY KEY,
 connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
 version integer NOT NULL CHECK(version > 0),
 captured_at timestamptz NOT NULL DEFAULT now(),
 checksum char(64) NOT NULL,
 catalog jsonb NOT NULL,
 source varchar(16) NOT NULL CHECK (source IN ('manual','scheduled')),
 UNIQUE(connection_id, version)
);
CREATE INDEX IF NOT EXISTS ix_metadata_snapshot_latest
 ON dynamics.metadata_snapshots(connection_id, version DESC);
CREATE TABLE IF NOT EXISTS dynamics.metadata_allowed_entities (
 connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
 logical_name varchar(128) NOT NULL,
 enabled boolean NOT NULL DEFAULT false,
 updated_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY (connection_id, logical_name),
 CONSTRAINT chk_catalog_entity_name CHECK (logical_name ~ '^[a-z][a-z0-9_]*$')
);
CREATE TABLE IF NOT EXISTS dynamics.metadata_allowed_fields (
 connection_id uuid NOT NULL,
 entity_name varchar(128) NOT NULL,
 field_name varchar(128) NOT NULL,
 enabled boolean NOT NULL DEFAULT false,
 can_read boolean NOT NULL DEFAULT false,
 can_create boolean NOT NULL DEFAULT false,
 can_update boolean NOT NULL DEFAULT false,
 metadata_fingerprint char(64) NOT NULL,
 updated_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(connection_id, entity_name, field_name),
 FOREIGN KEY(connection_id, entity_name)
  REFERENCES dynamics.metadata_allowed_entities(connection_id,logical_name) ON DELETE CASCADE,
 CONSTRAINT chk_allowed_field_name CHECK (field_name ~ '^[a-z][a-z0-9_]*$')
);
CREATE TABLE IF NOT EXISTS dynamics.metadata_refresh_schedules (
 connection_id uuid PRIMARY KEY REFERENCES dynamics.connections(id) ON DELETE CASCADE,
 enabled boolean NOT NULL DEFAULT false,
 interval_minutes integer NOT NULL DEFAULT 1440
  CHECK(interval_minutes BETWEEN 60 AND 10080),
 next_run_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now()
);
