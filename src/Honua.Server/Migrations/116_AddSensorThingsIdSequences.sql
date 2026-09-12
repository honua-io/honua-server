-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 116_AddSensorThingsIdSequences.sql
-- Description: Gives every SensorThings entity table a dedicated identifier sequence so
--              concurrent ingest cannot allocate the same @iot.id twice (#4199).
-- Dependencies: 059_CreateSensorThings.sql
--
-- Phase 2 ingest allocated ids with SELECT COALESCE(MAX(id), 0) + 1 inside a READ
-- COMMITTED transaction and no lock. Two overlapping writers read the same MAX and
-- allocate the same id. sta_observation is range-partitioned so its primary key is
-- (id, phenomenon_time): the duplicate insert SUCCEEDS whenever the phenomenon times
-- differ, and two observations — possibly in different datastreams — end up sharing one
-- @iot.id, so the Location/@iot.selfLink of one 201 response resolves to the other row.
-- The single-column catalog PKs turn the same race into a 23505 and a 500 instead.
--
-- nextval is non-transactional and never hands the same value to two sessions, so an
-- id sequence per table removes the read-then-write window entirely. Existing rows keep
-- their ids: each sequence is positioned at the current MAX(id) so the next allocation
-- continues the series. Deployments that already contain duplicate observation ids (the
-- silent outcome above) keep both rows — this migration stops new collisions rather than
-- renumbering observations whose @iot.selfLink clients may already hold.

CREATE SEQUENCE IF NOT EXISTS $HonuaSchema$.sta_thing_id_seq AS bigint START WITH 1 INCREMENT BY 1 NO CYCLE
    OWNED BY $HonuaSchema$.sta_thing.id;
CREATE SEQUENCE IF NOT EXISTS $HonuaSchema$.sta_sensor_id_seq AS bigint START WITH 1 INCREMENT BY 1 NO CYCLE
    OWNED BY $HonuaSchema$.sta_sensor.id;
CREATE SEQUENCE IF NOT EXISTS $HonuaSchema$.sta_observed_property_id_seq AS bigint START WITH 1 INCREMENT BY 1 NO CYCLE
    OWNED BY $HonuaSchema$.sta_observed_property.id;
CREATE SEQUENCE IF NOT EXISTS $HonuaSchema$.sta_datastream_id_seq AS bigint START WITH 1 INCREMENT BY 1 NO CYCLE
    OWNED BY $HonuaSchema$.sta_datastream.id;
CREATE SEQUENCE IF NOT EXISTS $HonuaSchema$.sta_observation_id_seq AS bigint START WITH 1 INCREMENT BY 1 NO CYCLE
    OWNED BY $HonuaSchema$.sta_observation.id;

-- The default on the partitioned parent is applied during tuple routing, so inserts
-- through $HonuaSchema$.sta_observation reach every existing and future partition.
ALTER TABLE $HonuaSchema$.sta_thing
    ALTER COLUMN id SET DEFAULT nextval('$HonuaSchema$.sta_thing_id_seq');
ALTER TABLE $HonuaSchema$.sta_sensor
    ALTER COLUMN id SET DEFAULT nextval('$HonuaSchema$.sta_sensor_id_seq');
ALTER TABLE $HonuaSchema$.sta_observed_property
    ALTER COLUMN id SET DEFAULT nextval('$HonuaSchema$.sta_observed_property_id_seq');
ALTER TABLE $HonuaSchema$.sta_datastream
    ALTER COLUMN id SET DEFAULT nextval('$HonuaSchema$.sta_datastream_id_seq');
ALTER TABLE $HonuaSchema$.sta_observation
    ALTER COLUMN id SET DEFAULT nextval('$HonuaSchema$.sta_observation_id_seq');

-- Position each sequence after the highest existing id. The third setval argument is
-- is_called: false on an empty table so the first allocation is 1, true otherwise so the
-- first allocation is MAX(id) + 1. Re-running is harmless because the sequence never
-- moves backwards past a live id.
SELECT setval('$HonuaSchema$.sta_thing_id_seq',
    GREATEST(COALESCE(MAX(id), 0), 1), COALESCE(MAX(id), 0) > 0) FROM $HonuaSchema$.sta_thing;
SELECT setval('$HonuaSchema$.sta_sensor_id_seq',
    GREATEST(COALESCE(MAX(id), 0), 1), COALESCE(MAX(id), 0) > 0) FROM $HonuaSchema$.sta_sensor;
SELECT setval('$HonuaSchema$.sta_observed_property_id_seq',
    GREATEST(COALESCE(MAX(id), 0), 1), COALESCE(MAX(id), 0) > 0) FROM $HonuaSchema$.sta_observed_property;
SELECT setval('$HonuaSchema$.sta_datastream_id_seq',
    GREATEST(COALESCE(MAX(id), 0), 1), COALESCE(MAX(id), 0) > 0) FROM $HonuaSchema$.sta_datastream;
SELECT setval('$HonuaSchema$.sta_observation_id_seq',
    GREATEST(COALESCE(MAX(id), 0), 1), COALESCE(MAX(id), 0) > 0) FROM $HonuaSchema$.sta_observation;
