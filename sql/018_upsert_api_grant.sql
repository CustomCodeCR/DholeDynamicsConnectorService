-- UPSERT is already supported by the keyed execution pipeline, but the
-- original API-grant CHECK still enumerated only create/read/update/delete.
-- This additive migration makes its least-privilege grant representable.
ALTER TABLE dynamics.api_grants DROP CONSTRAINT chk_grant_operation;
ALTER TABLE dynamics.api_grants
    ADD CONSTRAINT chk_grant_operation
    CHECK (operation IN ('create','read','update','upsert','delete'));
