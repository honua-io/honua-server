-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Every settings change is kept as a revision stamped with the configuration generation it produced.
-- The payload stays protected exactly as stored, so a restore copies bytes and never decrypts.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.configuration_document_revisions (
    revision_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind TEXT COLLATE "C" NOT NULL,
    key TEXT COLLATE "C" NOT NULL,
    change TEXT COLLATE "C" NOT NULL,
    document_revision BIGINT NULL,
    generation BIGINT NOT NULL,
    payload BYTEA NULL,
    restored_from BIGINT NULL,
    changed_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT configuration_document_revisions_change CHECK (change IN ('baseline', 'created', 'updated', 'deleted')),
    CONSTRAINT configuration_document_revisions_payload CHECK ((change = 'deleted') = (payload IS NULL)),
    CONSTRAINT configuration_document_revisions_document_revision CHECK ((change = 'deleted') = (document_revision IS NULL))
);

CREATE INDEX IF NOT EXISTS configuration_document_revisions_document
    ON $HonuaSchema$.configuration_document_revisions (kind, key, revision_id DESC);

-- Fires after configuration_documents_generation (triggers run in name order), so it records the
-- generation this change produced. Each document keeps its newest 32 revisions.
CREATE OR REPLACE FUNCTION $HonuaSchema$.record_configuration_document_revision() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    document_kind TEXT;
    document_key TEXT;
BEGIN
    IF TG_OP = 'DELETE' THEN
        document_kind := OLD.kind;
        document_key := OLD.key;
        INSERT INTO $HonuaSchema$.configuration_document_revisions (kind, key, change, document_revision, generation, payload)
        SELECT OLD.kind, OLD.key, 'deleted', NULL, generation, NULL
        FROM $HonuaSchema$.configuration_generation WHERE singleton;
    ELSE
        document_kind := NEW.kind;
        document_key := NEW.key;
        INSERT INTO $HonuaSchema$.configuration_document_revisions (kind, key, change, document_revision, generation, payload)
        SELECT NEW.kind, NEW.key, CASE WHEN TG_OP = 'INSERT' THEN 'created' ELSE 'updated' END, NEW.revision, generation, NEW.payload
        FROM $HonuaSchema$.configuration_generation WHERE singleton;
    END IF;
    DELETE FROM $HonuaSchema$.configuration_document_revisions
    WHERE kind = document_kind AND key = document_key AND revision_id < (
        SELECT min(kept.revision_id) FROM (
            SELECT revision_id FROM $HonuaSchema$.configuration_document_revisions
            WHERE kind = document_kind AND key = document_key
            ORDER BY revision_id DESC LIMIT 32) kept);
    RETURN NULL;
END;
$$;

CREATE OR REPLACE TRIGGER configuration_documents_revisions AFTER INSERT OR UPDATE OR DELETE
    ON $HonuaSchema$.configuration_documents FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.record_configuration_document_revision();

-- Documents written before this migration start with one baseline revision; a baseline has nothing to undo.
-- The trigger exists first, so a write committed meanwhile records its own revision and needs no baseline.
INSERT INTO $HonuaSchema$.configuration_document_revisions (kind, key, change, document_revision, generation, payload)
SELECT document.kind, document.key, 'baseline', document.revision, generation.generation, document.payload
FROM $HonuaSchema$.configuration_documents document
CROSS JOIN $HonuaSchema$.configuration_generation generation
WHERE generation.singleton
  AND NOT EXISTS (
      SELECT 1 FROM $HonuaSchema$.configuration_document_revisions existing
      WHERE existing.kind = document.kind AND existing.key = document.key);
