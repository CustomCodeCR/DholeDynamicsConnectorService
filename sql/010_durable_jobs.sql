-- Phase 11. Durable, isolated, payload-free orchestration (no Dhole Redis or shared queues).
CREATE TABLE dynamics.job_queue (
 id uuid PRIMARY KEY,
 connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
 kind varchar(32) NOT NULL CHECK(kind IN ('delta_sync','metadata_refresh','legacy_preview','legacy_import','reconcile_scan')),
 resource_key varchar(128) NOT NULL DEFAULT '',
 dedupe_key varchar(128) NOT NULL CHECK(length(dedupe_key) >= 16),
 status varchar(16) NOT NULL DEFAULT 'pending'
   CHECK(status IN ('pending','running','completed','dead')),
 attempts integer NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 100),
 max_attempts integer NOT NULL DEFAULT 5 CHECK(max_attempts BETWEEN 1 AND 20),
 available_at timestamptz NOT NULL DEFAULT now(),
 lease_owner uuid,
 lease_until timestamptz,
 lease_version bigint NOT NULL DEFAULT 0,
 last_error_code varchar(64),
 page_count integer NOT NULL DEFAULT 0,
 created_by varchar(128) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now(),
 finished_at timestamptz,
 UNIQUE(connection_id,kind,resource_key,dedupe_key),
 CHECK(status = 'running' OR (lease_owner IS NULL AND lease_until IS NULL))
);
CREATE UNIQUE INDEX ux_job_active ON dynamics.job_queue(connection_id,kind,resource_key)
 WHERE status IN ('pending','running');
CREATE INDEX ix_jobs_claim ON dynamics.job_queue(available_at,created_at)
 WHERE status IN ('pending','running');
CREATE INDEX ix_jobs_lookup ON dynamics.job_queue(connection_id,created_at DESC);

CREATE TABLE dynamics.sync_config (
 connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
 entity_name varchar(128) NOT NULL,
 enabled boolean NOT NULL DEFAULT false,
 mode varchar(16) NOT NULL CHECK(mode IN ('delta','watermark')),
 fallback_to_watermark boolean NOT NULL DEFAULT false,
 interval_minutes integer NOT NULL CHECK(interval_minutes BETWEEN 15 AND 10080),
 next_run_at timestamptz NOT NULL DEFAULT now(),
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(connection_id,entity_name),
 FOREIGN KEY(connection_id,entity_name)
   REFERENCES dynamics.metadata_allowed_entities(connection_id,logical_name) ON DELETE CASCADE
);
CREATE TABLE dynamics.sync_checkpoints (
 connection_id uuid NOT NULL,
 entity_name varchar(128) NOT NULL,
 generation bigint NOT NULL DEFAULT 0,
 mode varchar(16) NOT NULL DEFAULT 'delta',
 next_link text,
 delta_link text,
 watermark_utc timestamptz,
 scan_started_at timestamptz,
 last_success_at timestamptz,
 pages_processed bigint NOT NULL DEFAULT 0,
 events_processed bigint NOT NULL DEFAULT 0,
 PRIMARY KEY(connection_id,entity_name),
 FOREIGN KEY(connection_id,entity_name)
   REFERENCES dynamics.sync_config(connection_id,entity_name) ON DELETE CASCADE
);
-- Only GUIDs and hashes of provider ETags. Never persist business record bodies / PII.
CREATE TABLE dynamics.sync_changes (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 connection_id uuid NOT NULL,
 entity_name varchar(128) NOT NULL,
 record_id uuid NOT NULL,
 change_hash char(64) NOT NULL CHECK(change_hash ~ '^[A-F0-9]{64}$'),
 action varchar(8) NOT NULL CHECK(action IN ('upsert','delete')),
 observed_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(connection_id,entity_name,record_id,change_hash,action),
 FOREIGN KEY(connection_id,entity_name)
   REFERENCES dynamics.sync_config(connection_id,entity_name) ON DELETE CASCADE
);
CREATE INDEX ix_sync_changes_recent ON dynamics.sync_changes(connection_id,entity_name,observed_at DESC);
CREATE INDEX ix_sync_changes_poll ON dynamics.sync_changes(connection_id,entity_name,id);
CREATE TABLE dynamics.job_alerts (
 id uuid PRIMARY KEY,
 connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
 job_id uuid NULL REFERENCES dynamics.job_queue(id) ON DELETE SET NULL,
 code varchar(64) NOT NULL,
 acknowledged_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_job_alerts_unack ON dynamics.job_alerts(created_at DESC)
 WHERE acknowledged_at IS NULL;
CREATE TABLE dynamics.job_circuits (
 connection_id uuid PRIMARY KEY REFERENCES dynamics.connections(id) ON DELETE CASCADE,
 consecutive_failures integer NOT NULL DEFAULT 0,
 open_until timestamptz,
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE dynamics.operational_retention (
 id integer PRIMARY KEY DEFAULT 1 CHECK(id = 1),
 job_days integer NOT NULL DEFAULT 30 CHECK(job_days BETWEEN 7 AND 365),
 observation_days integer NOT NULL DEFAULT 30 CHECK(observation_days BETWEEN 7 AND 365),
 change_days integer NOT NULL DEFAULT 30 CHECK(change_days BETWEEN 7 AND 365),
 updated_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO dynamics.operational_retention(id) VALUES(1) ON CONFLICT DO NOTHING;
