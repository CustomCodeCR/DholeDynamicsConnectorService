-- Phase 08: sanitized request observability, no payloads, user agents, full URLs or credentials.
CREATE TABLE IF NOT EXISTS dynamics.http_observations (
 id uuid PRIMARY KEY,
 correlation_id uuid NOT NULL,
 route_pattern varchar(250) NOT NULL,
 method varchar(12) NOT NULL,
 status_code integer NOT NULL CHECK(status_code BETWEEN 100 AND 599),
 elapsed_ms integer NOT NULL CHECK(elapsed_ms BETWEEN 0 AND 3600000),
 observed_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_http_observations_recent
 ON dynamics.http_observations(observed_at DESC);
CREATE INDEX IF NOT EXISTS ix_http_observations_correlation
 ON dynamics.http_observations(correlation_id);
