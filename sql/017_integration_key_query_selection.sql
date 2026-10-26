-- An integration key may authorize a selected set of saved-query names.
-- Existing all/single keys retain their exact semantics.
ALTER TABLE dynamics.saved_query_api_keys
    DROP CONSTRAINT chk_saved_key_scope;

ALTER TABLE dynamics.saved_query_api_keys
    ADD CONSTRAINT chk_saved_key_scope CHECK (
        (key_scope = 'single'
            AND saved_query_name IS NOT NULL
            AND saved_query_updated_at IS NOT NULL
            AND saved_query_name ~ '^[a-z][a-z0-9_-]{0,79}$')
        OR (key_scope IN ('all', 'selected')
            AND saved_query_name IS NULL
            AND saved_query_updated_at IS NULL)
    );

CREATE TABLE dynamics.saved_query_key_queries (
    key_id uuid NOT NULL REFERENCES dynamics.saved_query_api_keys(id) ON DELETE CASCADE,
    query_name varchar(80) NOT NULL CHECK (query_name ~ '^[a-z][a-z0-9_-]{0,79}$'),
    PRIMARY KEY(key_id,query_name)
);
