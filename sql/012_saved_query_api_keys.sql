-- Scoped, revocable, high-entropy API keys for executing only approved saved reads.
-- No plaintext credential is persisted. Never store an OAuth or Dhole user token.
CREATE TABLE dynamics.saved_query_api_keys (
    id uuid PRIMARY KEY,
    connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
    saved_query_name varchar(80) NOT NULL,
    key_sha256 bytea NOT NULL UNIQUE CHECK (octet_length(key_sha256)=32),
    label varchar(120) NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    max_requests_per_minute integer NOT NULL CHECK(max_requests_per_minute BETWEEN 1 AND 120),
    expires_at timestamptz NOT NULL,
    created_by varchar(128) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz NULL,
    CONSTRAINT chk_saved_key_name CHECK (saved_query_name ~ '^[a-z][a-z0-9_-]{0,79}$')
);
CREATE INDEX ix_saved_query_api_keys_connection
 ON dynamics.saved_query_api_keys(connection_id,created_at DESC);
CREATE TABLE dynamics.saved_query_api_key_quota (
    key_id uuid NOT NULL REFERENCES dynamics.saved_query_api_keys(id) ON DELETE CASCADE,
    window_minute bigint NOT NULL,
    request_count integer NOT NULL CHECK(request_count > 0),
    PRIMARY KEY(key_id,window_minute)
);
CREATE INDEX ix_saved_query_api_key_quota_minutes
 ON dynamics.saved_query_api_key_quota(window_minute);
