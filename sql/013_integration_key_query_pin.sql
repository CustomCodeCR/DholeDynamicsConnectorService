-- Bind issued keys to the exact saved query revision. Editing or re-enabling
-- the query invalidates prior keys automatically, even if still named identically.
ALTER TABLE dynamics.saved_query_api_keys
  ADD COLUMN saved_query_updated_at timestamptz;
UPDATE dynamics.saved_query_api_keys k
SET saved_query_updated_at=q.updated_at
FROM dynamics.saved_queries q
WHERE q.connection_id=k.connection_id AND q.name=k.saved_query_name;
DELETE FROM dynamics.saved_query_api_keys WHERE saved_query_updated_at IS NULL;
ALTER TABLE dynamics.saved_query_api_keys
  ALTER COLUMN saved_query_updated_at SET NOT NULL;
