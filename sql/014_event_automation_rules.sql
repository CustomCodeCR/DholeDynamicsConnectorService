-- Event-triggered Dataverse command blueprints. No automatic writes are enabled
-- by a draft. Only approved source/target combinations can be persisted.
CREATE TABLE dynamics.event_automation_rules (
  id uuid PRIMARY KEY,
  connection_id uuid NOT NULL REFERENCES dynamics.connections(id) ON DELETE CASCADE,
  name varchar(80) NOT NULL CHECK(name ~ '^[a-z][a-z0-9_-]{0,79}$'),
  trigger_code varchar(64) NOT NULL CHECK(trigger_code IN
    ('pricing.rate.accepted_by_client','pricing.rate.margin_approved')),
  template_name varchar(80) NOT NULL CHECK(template_name ~ '^[A-Za-z][A-Za-z0-9_-]{0,79}$'),
  mapping jsonb NOT NULL CHECK(jsonb_typeof(mapping)='array'),
  enabled boolean NOT NULL DEFAULT false,
  created_by text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(connection_id,name),
  CONSTRAINT chk_event_rules_never_autoenabled CHECK(enabled=false)
);
CREATE INDEX ix_event_rules_connection ON dynamics.event_automation_rules(connection_id,trigger_code);
