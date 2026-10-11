-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Preserve legacy IDs, numeric results and timestamps. No historical feature of
-- interest is inferred from a Thing's current location. Unresolved rows remain
-- readable and must be explicitly reconciled before enabling a conformant profile.
ALTER TABLE $HonuaSchema$.sta_thing ADD COLUMN IF NOT EXISTS properties jsonb;
ALTER TABLE $HonuaSchema$.sta_sensor ADD COLUMN IF NOT EXISTS properties jsonb;
ALTER TABLE $HonuaSchema$.sta_sensor ADD COLUMN IF NOT EXISTS metadata_json jsonb;
UPDATE $HonuaSchema$.sta_sensor SET metadata_json = to_jsonb(metadata) WHERE metadata_json IS NULL;
ALTER TABLE $HonuaSchema$.sta_observed_property ADD COLUMN IF NOT EXISTS properties jsonb;
ALTER TABLE $HonuaSchema$.sta_datastream ADD COLUMN IF NOT EXISTS properties jsonb;
ALTER TABLE $HonuaSchema$.sta_datastream ADD COLUMN IF NOT EXISTS observed_area jsonb;
ALTER TABLE $HonuaSchema$.sta_datastream ALTER COLUMN unit_name DROP NOT NULL;
ALTER TABLE $HonuaSchema$.sta_datastream ALTER COLUMN unit_symbol DROP NOT NULL;
ALTER TABLE $HonuaSchema$.sta_datastream ALTER COLUMN unit_definition DROP NOT NULL;
ALTER TABLE $HonuaSchema$.sta_observation ADD COLUMN IF NOT EXISTS result_json jsonb;
ALTER TABLE $HonuaSchema$.sta_observation ADD COLUMN IF NOT EXISTS phenomenon_time_end timestamptz;
ALTER TABLE $HonuaSchema$.sta_observation ADD COLUMN IF NOT EXISTS valid_time text;
ALTER TABLE $HonuaSchema$.sta_observation ADD COLUMN IF NOT EXISTS result_quality jsonb;
ALTER TABLE $HonuaSchema$.sta_observation ADD COLUMN IF NOT EXISTS parameters jsonb;
ALTER TABLE $HonuaSchema$.sta_observation ALTER COLUMN result DROP NOT NULL;
UPDATE $HonuaSchema$.sta_observation SET result_json = to_jsonb(result) WHERE result_json IS NULL;

-- Keep the legacy numeric projection consistent for existing canonical callers
-- and data imports. JSON is authoritative when supplied, including JSON null.
CREATE OR REPLACE FUNCTION $HonuaSchema$.sta_synchronize_result() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.result_json IS NULL THEN
        NEW.result_json := to_jsonb(NEW.result);
    ELSIF TG_OP = 'UPDATE' AND NEW.result IS DISTINCT FROM OLD.result AND NEW.result_json IS NOT DISTINCT FROM OLD.result_json THEN
        NEW.result_json := to_jsonb(NEW.result);
    ELSE
        NEW.result := CASE WHEN jsonb_typeof(NEW.result_json) = 'number' THEN (NEW.result_json #>> '{}')::double precision ELSE NULL END;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER sta_observation_result_sync BEFORE INSERT OR UPDATE OF result, result_json
    ON $HonuaSchema$.sta_observation FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.sta_synchronize_result();

CREATE SEQUENCE IF NOT EXISTS $HonuaSchema$.sta_location_id_seq;
CREATE SEQUENCE IF NOT EXISTS $HonuaSchema$.sta_historical_location_id_seq;
CREATE SEQUENCE IF NOT EXISTS $HonuaSchema$.sta_feature_of_interest_id_seq;
CREATE TABLE IF NOT EXISTS $HonuaSchema$.sta_location (
    id bigint PRIMARY KEY DEFAULT nextval('$HonuaSchema$.sta_location_id_seq'),
    name text NOT NULL, description text NOT NULL, encoding_type text NOT NULL,
    location jsonb NOT NULL, properties jsonb
);
CREATE TABLE IF NOT EXISTS $HonuaSchema$.sta_historical_location (
    id bigint PRIMARY KEY DEFAULT nextval('$HonuaSchema$.sta_historical_location_id_seq'),
    time timestamptz NOT NULL,
    thing_id bigint NOT NULL REFERENCES $HonuaSchema$.sta_thing(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS $HonuaSchema$.sta_feature_of_interest (
    id bigint PRIMARY KEY DEFAULT nextval('$HonuaSchema$.sta_feature_of_interest_id_seq'),
    name text NOT NULL, description text NOT NULL, encoding_type text NOT NULL,
    feature jsonb NOT NULL, properties jsonb,
    source_location_id bigint REFERENCES $HonuaSchema$.sta_location(id) ON DELETE SET NULL
);
CREATE TABLE IF NOT EXISTS $HonuaSchema$.sta_thing_location (
    thing_id bigint NOT NULL REFERENCES $HonuaSchema$.sta_thing(id) ON DELETE CASCADE,
    location_id bigint NOT NULL REFERENCES $HonuaSchema$.sta_location(id) ON DELETE CASCADE,
    PRIMARY KEY (thing_id, location_id)
);
CREATE TABLE IF NOT EXISTS $HonuaSchema$.sta_historical_location_location (
    historical_location_id bigint NOT NULL REFERENCES $HonuaSchema$.sta_historical_location(id) ON DELETE CASCADE,
    location_id bigint NOT NULL REFERENCES $HonuaSchema$.sta_location(id) ON DELETE CASCADE,
    PRIMARY KEY (historical_location_id, location_id)
);
CREATE INDEX IF NOT EXISTS ix_sta_history_thing_time ON $HonuaSchema$.sta_historical_location(thing_id, time);
CREATE INDEX IF NOT EXISTS ix_sta_observation_foi ON $HonuaSchema$.sta_observation(feature_of_interest_id);

-- Preserve unresolved source references separately from trustworthy associations.
-- Valid parent FKs work on PostgreSQL 16 and protect existing and future partitions.
ALTER TABLE $HonuaSchema$.sta_observation ADD COLUMN IF NOT EXISTS datastream_reference_id bigint;
ALTER TABLE $HonuaSchema$.sta_observation ADD COLUMN IF NOT EXISTS feature_of_interest_reference_id bigint;
CREATE INDEX IF NOT EXISTS ix_sta_observation_datastream_reference_time ON $HonuaSchema$.sta_observation(datastream_reference_id,phenomenon_time,id);
CREATE INDEX IF NOT EXISTS ix_sta_observation_feature_reference ON $HonuaSchema$.sta_observation(feature_of_interest_reference_id);
UPDATE $HonuaSchema$.sta_observation o SET datastream_reference_id=o.datastream_id
    FROM $HonuaSchema$.sta_datastream d WHERE d.id=o.datastream_id;
-- The new FoI table starts empty: legacy references never become associations merely
-- because a later entity happens to receive the same identifier.
SELECT setval('$HonuaSchema$.sta_feature_of_interest_id_seq',
    GREATEST(COALESCE((SELECT MAX(feature_of_interest_id) FROM $HonuaSchema$.sta_observation),0),
             COALESCE((SELECT MAX(id) FROM $HonuaSchema$.sta_feature_of_interest),0),1),
    GREATEST(COALESCE((SELECT MAX(feature_of_interest_id) FROM $HonuaSchema$.sta_observation),0),
             COALESCE((SELECT MAX(id) FROM $HonuaSchema$.sta_feature_of_interest),0))>0);
SELECT setval('$HonuaSchema$.sta_datastream_id_seq',
    GREATEST(COALESCE((SELECT MAX(datastream_id) FROM $HonuaSchema$.sta_observation),0),
             COALESCE((SELECT MAX(id) FROM $HonuaSchema$.sta_datastream),0),1),true);
ALTER TABLE $HonuaSchema$.sta_observation ADD CONSTRAINT sta_observation_datastream_fk
    FOREIGN KEY (datastream_reference_id) REFERENCES $HonuaSchema$.sta_datastream(id) ON DELETE CASCADE;
ALTER TABLE $HonuaSchema$.sta_observation ADD CONSTRAINT sta_observation_foi_fk
    FOREIGN KEY (feature_of_interest_reference_id) REFERENCES $HonuaSchema$.sta_feature_of_interest(id) ON DELETE CASCADE;
ALTER TABLE $HonuaSchema$.sta_observation ADD CONSTRAINT sta_observation_reference_consistency
    CHECK ((datastream_reference_id IS NULL OR (datastream_id IS NOT NULL AND datastream_reference_id=datastream_id))
       AND (feature_of_interest_reference_id IS NULL OR (feature_of_interest_id IS NOT NULL AND feature_of_interest_reference_id=feature_of_interest_id)));
CREATE OR REPLACE FUNCTION $HonuaSchema$.sta_synchronize_references() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        NEW.datastream_reference_id := NEW.datastream_id;
        NEW.feature_of_interest_reference_id := NEW.feature_of_interest_id;
    ELSE
        IF NEW.datastream_id IS DISTINCT FROM OLD.datastream_id THEN
            NEW.datastream_reference_id := NEW.datastream_id;
        END IF;
        IF NEW.feature_of_interest_id IS DISTINCT FROM OLD.feature_of_interest_id THEN
            NEW.feature_of_interest_reference_id := NEW.feature_of_interest_id;
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER sta_observation_reference_sync BEFORE INSERT OR UPDATE OF datastream_id,feature_of_interest_id
    ON $HonuaSchema$.sta_observation FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.sta_synchronize_references();
