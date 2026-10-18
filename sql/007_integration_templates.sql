-- Phase 07: versioned definitions, activation pointers, and connection-bound Dhole/Dataverse references.
CREATE TABLE dynamics.integration_templates (
    id uuid PRIMARY KEY,
    connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
    name varchar(80) NOT NULL CHECK(name ~ '^[a-z][a-z0-9_-]{0,79}$'),
    active_version integer,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(connection_id,name)
);
CREATE TABLE dynamics.template_versions (
    template_id uuid NOT NULL REFERENCES dynamics.integration_templates(id) ON DELETE CASCADE,
    version integer NOT NULL CHECK(version > 0),
    definition jsonb NOT NULL CHECK(jsonb_typeof(definition) = 'object'),
    definition_sha256 char(64) NOT NULL CHECK(definition_sha256 ~ '^[A-F0-9]{64}$'),
    metadata_version integer NOT NULL CHECK(metadata_version > 0),
    created_by text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(template_id,version)
);
ALTER TABLE dynamics.integration_templates
    ADD CONSTRAINT fk_template_active_version
    FOREIGN KEY(id,active_version) REFERENCES dynamics.template_versions(template_id,version)
    DEFERRABLE INITIALLY DEFERRED;
CREATE TABLE dynamics.external_references (
    connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
    reference_space varchar(64) NOT NULL CHECK(reference_space ~ '^[a-z][a-z0-9_-]*$'),
    internal_id varchar(128) NOT NULL CHECK(length(internal_id)>0),
    entity_name varchar(128) NOT NULL CHECK(entity_name ~ '^[a-z][a-z0-9_]*$'),
    dataverse_id uuid NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(connection_id,reference_space,internal_id,entity_name),
    UNIQUE(connection_id,reference_space,dataverse_id,entity_name)
);
CREATE INDEX ix_template_versions_template_created ON dynamics.template_versions(template_id,version DESC);

-- Named, bound Dataverse actions are opt-in and cannot become an open HTTP proxy.
CREATE TABLE dynamics.allowed_actions (
    connection_id uuid NOT NULL,
    entity_name varchar(128) NOT NULL,
    action_name varchar(80) NOT NULL CHECK(action_name ~ '^[A-Za-z][A-Za-z0-9_]{0,79}$'),
    parameters jsonb NOT NULL CHECK(jsonb_typeof(parameters) = 'array'),
    enabled boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(connection_id,entity_name,action_name),
    FOREIGN KEY(connection_id,entity_name)
      REFERENCES dynamics.metadata_allowed_entities(connection_id,logical_name) ON DELETE CASCADE
);
ALTER TABLE dynamics.api_grants DROP CONSTRAINT IF EXISTS chk_grant_operation;
ALTER TABLE dynamics.api_grants ADD CONSTRAINT chk_grant_operation
 CHECK(operation IN ('create','read','update','delete','upsert','action'));
ALTER TABLE dynamics.write_operations DROP CONSTRAINT IF EXISTS write_operations_operation_check;
ALTER TABLE dynamics.write_operations ADD CONSTRAINT write_operations_operation_check
 CHECK(operation IN ('create','update','delete','upsert','batch','action'));
