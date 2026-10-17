-- Phase 05: saved, validated query templates, never raw OData or SQL.
CREATE TABLE IF NOT EXISTS dynamics.saved_queries (
  id uuid PRIMARY KEY,
  connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
  name varchar(80) NOT NULL,
  entity_name varchar(128) NOT NULL,
  spec jsonb NOT NULL,
  parameter_schema jsonb NOT NULL,
  enabled boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(connection_id,name),
  CONSTRAINT chk_saved_name CHECK (name ~ '^[a-z][a-z0-9_-]{0,79}$'),
  CONSTRAINT chk_saved_entity CHECK (entity_name ~ '^[a-z][a-z0-9_]*$'),
  CONSTRAINT chk_saved_json CHECK (jsonb_typeof(spec)='object' AND jsonb_typeof(parameter_schema)='array')
);
CREATE INDEX IF NOT EXISTS ix_saved_queries_connection
  ON dynamics.saved_queries(connection_id,enabled);
