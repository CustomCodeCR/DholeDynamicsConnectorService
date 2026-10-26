-- A connection-scoped integration key can read every currently enabled saved query.
-- Previously issued keys stay restricted to their query and pinned revision.
ALTER TABLE dynamics.saved_query_api_keys
    ADD COLUMN key_scope varchar(12) NOT NULL DEFAULT 'single',
    ALTER COLUMN saved_query_name DROP NOT NULL,
    ALTER COLUMN saved_query_updated_at DROP NOT NULL;

ALTER TABLE dynamics.saved_query_api_keys
    DROP CONSTRAINT chk_saved_key_name;

ALTER TABLE dynamics.saved_query_api_keys
    ADD CONSTRAINT chk_saved_key_scope CHECK (
        (key_scope = 'single'
            AND saved_query_name IS NOT NULL
            AND saved_query_updated_at IS NOT NULL
            AND saved_query_name ~ '^[a-z][a-z0-9_-]{0,79}$')
        OR (key_scope = 'all'
            AND saved_query_name IS NULL
            AND saved_query_updated_at IS NULL)
    );
