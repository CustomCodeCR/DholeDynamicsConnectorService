-- Phase 09. Only migration reports; no imported tokens or client secrets.
CREATE TABLE IF NOT EXISTS dynamics.legacy_migration_items (
    connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
    source_id varchar(16) NOT NULL CHECK(source_id ~ '^LEG-[0-9]{3}$'),
    package_format varchar(48) NOT NULL,
    status varchar(40) NOT NULL,
    reasons jsonb NOT NULL DEFAULT '[]'::jsonb CHECK(jsonb_typeof(reasons) = 'array'),
    metadata_version integer,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(connection_id, source_id)
);
CREATE INDEX IF NOT EXISTS ix_legacy_migration_status
 ON dynamics.legacy_migration_items(connection_id,status);
