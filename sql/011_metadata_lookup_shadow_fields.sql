-- Permit only Dataverse-generated lookup projection fields (_<logical name>_value).
-- They remain read-only in metadata policy and require explicit per-connection grants.
-- Existing ordinary field-name restrictions remain unchanged.
ALTER TABLE dynamics.metadata_allowed_fields
  DROP CONSTRAINT chk_allowed_field_name;
ALTER TABLE dynamics.metadata_allowed_fields
  ADD CONSTRAINT chk_allowed_field_name
  CHECK (field_name ~ '^[a-z][a-z0-9_]*$'
      OR field_name ~ '^_[a-z][a-z0-9_]*_value$');
