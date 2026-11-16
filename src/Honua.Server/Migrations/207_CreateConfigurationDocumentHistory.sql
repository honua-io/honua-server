-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.configuration_document_history (
    kind TEXT COLLATE "C" NOT NULL,
    key TEXT COLLATE "C" NOT NULL,
    generation BIGINT NOT NULL CHECK (generation >= 0),
    payload BYTEA,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (kind, key, generation)
);

SELECT generation FROM $HonuaSchema$.configuration_generation WHERE singleton FOR UPDATE;

INSERT INTO $HonuaSchema$.configuration_document_history (kind, key, generation, payload)
SELECT document.kind, document.key, current.generation, document.payload
FROM $HonuaSchema$.configuration_documents document
CROSS JOIN $HonuaSchema$.configuration_generation current
WHERE current.singleton AND NOT EXISTS (
    SELECT 1 FROM $HonuaSchema$.configuration_document_history history
    WHERE history.kind = document.kind AND history.key = document.key
);

CREATE OR REPLACE FUNCTION $HonuaSchema$.advance_configuration_generation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE next_generation BIGINT;
BEGIN
    UPDATE $HonuaSchema$.configuration_generation SET generation = generation + 1
    WHERE singleton RETURNING generation INTO next_generation;
    IF TG_TABLE_NAME = 'configuration_documents' THEN
        IF TG_OP = 'DELETE' THEN
            INSERT INTO $HonuaSchema$.configuration_document_history (kind, key, generation, payload)
            VALUES (OLD.kind, OLD.key, next_generation, NULL);
        ELSE
            INSERT INTO $HonuaSchema$.configuration_document_history (kind, key, generation, payload)
            VALUES (NEW.kind, NEW.key, next_generation, NEW.payload);
        END IF;
    END IF;
    RETURN NULL;
END;
$$;
