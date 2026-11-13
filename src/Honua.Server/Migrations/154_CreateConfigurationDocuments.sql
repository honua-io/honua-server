-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.configuration_generation (
    singleton BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (singleton),
    generation BIGINT NOT NULL DEFAULT 0 CHECK (generation >= 0)
);
INSERT INTO $HonuaSchema$.configuration_generation (singleton) VALUES (TRUE) ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.configuration_documents (
    kind TEXT COLLATE "C" NOT NULL,
    key TEXT COLLATE "C" NOT NULL,
    payload BYTEA NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1 CHECK (revision > 0),
    PRIMARY KEY (kind, key)
);

CREATE OR REPLACE FUNCTION $HonuaSchema$.advance_configuration_generation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE $HonuaSchema$.configuration_generation SET generation = generation + 1 WHERE singleton;
    RETURN NULL;
END;
$$;
CREATE OR REPLACE TRIGGER configuration_documents_generation AFTER INSERT OR UPDATE OR DELETE
    ON $HonuaSchema$.configuration_documents FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.advance_configuration_generation();

CREATE OR REPLACE TRIGGER metadata_current_generation AFTER INSERT OR UPDATE OR DELETE
    ON $HonuaSchema$.metadata_v2_current FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.advance_configuration_generation();
