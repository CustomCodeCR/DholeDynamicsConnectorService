-- F6.10 additive, optional Dynamics job realtime Outbox.
-- No Dataverse payload, credential or business data is stored here.
-- Enabled only when coordinated release flags authorize the producer.
CREATE TABLE IF NOT EXISTS dynamics.realtime_job_outbox (
    event_id uuid PRIMARY KEY,
    connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
    job_id uuid NOT NULL REFERENCES dynamics.job_queue(id) ON DELETE CASCADE,
    payload_json jsonb NOT NULL,
    status varchar(16) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending','processed')),
    retry_count integer NOT NULL DEFAULT 0 CHECK (retry_count >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    processed_at timestamptz,
    CONSTRAINT chk_dynamics_realtime_json_object CHECK (jsonb_typeof(payload_json) = 'object')
);
CREATE INDEX IF NOT EXISTS ix_dynamics_realtime_pending
    ON dynamics.realtime_job_outbox(created_at,event_id)
    WHERE status='pending';
CREATE INDEX IF NOT EXISTS ix_dynamics_realtime_connection_job
    ON dynamics.realtime_job_outbox(connection_id,job_id);
