-- Phase 02: additive only. Never drops or rewrites legacy connection rows.
CREATE TABLE IF NOT EXISTS dynamics.secret_rotation_runs (
    id uuid PRIMARY KEY,
    started_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz NULL,
    target_version integer NOT NULL CHECK (target_version > 0),
    rotated_count integer NOT NULL DEFAULT 0 CHECK (rotated_count >= 0),
    status varchar(20) NOT NULL CHECK (status IN ('RUNNING', 'COMPLETED', 'FAILED'))
);
CREATE INDEX IF NOT EXISTS ix_secret_rotation_runs_started
    ON dynamics.secret_rotation_runs (started_at DESC);
