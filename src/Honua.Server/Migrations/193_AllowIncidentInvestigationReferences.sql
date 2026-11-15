CREATE TABLE IF NOT EXISTS $HonuaSchema$.investigation_incident_links (
    link_id BIGINT PRIMARY KEY DEFAULT nextval('$HonuaSchema$.investigation_links_link_id_seq'::regclass),
    investigation_id TEXT NOT NULL REFERENCES $HonuaSchema$.investigations(investigation_id) ON DELETE CASCADE,
    resource_kind SMALLINT NOT NULL DEFAULT 5 CHECK (resource_kind = 5),
    resource_id TEXT NOT NULL CHECK (length(resource_id) > 0),
    note TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_investigation_incident_links_investigation
    ON $HonuaSchema$.investigation_incident_links (investigation_id, link_id);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.investigation_incident_pins (
    pin_id BIGINT PRIMARY KEY DEFAULT nextval('$HonuaSchema$.investigation_pins_pin_id_seq'::regclass),
    investigation_id TEXT NOT NULL REFERENCES $HonuaSchema$.investigations(investigation_id) ON DELETE CASCADE,
    event_ref TEXT NOT NULL CHECK (length(event_ref) > 0),
    event_kind SMALLINT NOT NULL DEFAULT 8 CHECK (event_kind = 8),
    occurred_at TIMESTAMPTZ NOT NULL,
    note TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_investigation_incident_pins_investigation
    ON $HonuaSchema$.investigation_incident_pins (investigation_id, pin_id);
