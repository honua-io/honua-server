-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- OGC SensorThings API (STA v1.1) Phase 1 storage (#1747).
--
-- Catalog entities (Things, Sensors, ObservedProperties, Datastreams) are
-- low-cardinality reference data and live in plain catalog tables. Observations
-- are the high-frequency time-series and live in a dedicated table partitioned
-- by RANGE(phenomenon_time) per the SensorThings spike doc; a BRIN index on the
-- partition key keeps temporal-range scans cheap at IoT volumes without the
-- per-row overhead of the feature CDC path.

CREATE TABLE IF NOT EXISTS honua.sta_thing (
    id          bigint      NOT NULL,
    name        text        NOT NULL,
    description text        NOT NULL DEFAULT '',
    CONSTRAINT sta_thing_pkey PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS honua.sta_sensor (
    id            bigint      NOT NULL,
    name          text        NOT NULL,
    description   text        NOT NULL DEFAULT '',
    encoding_type text        NOT NULL DEFAULT 'application/pdf',
    metadata      text        NOT NULL DEFAULT '',
    CONSTRAINT sta_sensor_pkey PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS honua.sta_observed_property (
    id          bigint      NOT NULL,
    name        text        NOT NULL,
    definition  text        NOT NULL DEFAULT '',
    description text        NOT NULL DEFAULT '',
    CONSTRAINT sta_observed_property_pkey PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS honua.sta_datastream (
    id                    bigint      NOT NULL,
    name                  text        NOT NULL,
    description           text        NOT NULL DEFAULT '',
    observation_type      text        NOT NULL DEFAULT 'http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement',
    unit_name             text        NOT NULL DEFAULT '',
    unit_symbol           text        NOT NULL DEFAULT '',
    unit_definition       text        NOT NULL DEFAULT '',
    thing_id              bigint      NOT NULL,
    sensor_id             bigint      NOT NULL,
    observed_property_id  bigint      NOT NULL,
    CONSTRAINT sta_datastream_pkey PRIMARY KEY (id),
    CONSTRAINT sta_datastream_thing_fk FOREIGN KEY (thing_id) REFERENCES honua.sta_thing (id),
    CONSTRAINT sta_datastream_sensor_fk FOREIGN KEY (sensor_id) REFERENCES honua.sta_sensor (id),
    CONSTRAINT sta_datastream_obsprop_fk FOREIGN KEY (observed_property_id) REFERENCES honua.sta_observed_property (id)
);

-- Time-series observations, range-partitioned on phenomenon_time. id is unique
-- within a datastream; the partition key participates in the primary key as
-- required by declarative partitioning.
CREATE TABLE IF NOT EXISTS honua.sta_observation (
    id                     bigint      NOT NULL,
    datastream_id          bigint      NOT NULL,
    phenomenon_time        timestamptz NOT NULL,
    result_time            timestamptz,
    result                 double precision NOT NULL,
    feature_of_interest_id bigint,
    CONSTRAINT sta_observation_pkey PRIMARY KEY (id, phenomenon_time)
) PARTITION BY RANGE (phenomenon_time);

-- Default partition catches any phenomenon_time not covered by an explicit
-- partition so Phase 1 reads/seeds work without partition maintenance tooling.
CREATE TABLE IF NOT EXISTS honua.sta_observation_default
    PARTITION OF honua.sta_observation DEFAULT;

-- BRIN on the partition key is ideal for monotonically-increasing time-series.
CREATE INDEX IF NOT EXISTS ix_sta_observation_time
    ON honua.sta_observation USING BRIN (phenomenon_time);

-- Per-datastream temporal navigation (Datastreams(id)/Observations ordered by time).
CREATE INDEX IF NOT EXISTS ix_sta_observation_datastream_time
    ON honua.sta_observation (datastream_id, phenomenon_time);

-- Synthetic / simulated datastream so the read path is demoable end-to-end
-- without an ingest pipeline. Idempotent: ON CONFLICT DO NOTHING.
INSERT INTO honua.sta_thing (id, name, description)
VALUES (1, 'Demo Air Quality Station', 'Synthetic air-quality monitoring station for the SensorThings Phase 1 demo')
ON CONFLICT (id) DO NOTHING;

INSERT INTO honua.sta_sensor (id, name, description, encoding_type, metadata)
VALUES (1, 'Demo Thermometer', 'Synthetic temperature sensor', 'application/pdf', 'https://example.org/sensors/demo-thermometer')
ON CONFLICT (id) DO NOTHING;

INSERT INTO honua.sta_observed_property (id, name, definition, description)
VALUES (1, 'Air Temperature', 'http://mmisw.org/ont/cf/parameter/air_temperature', 'Ambient air temperature')
ON CONFLICT (id) DO NOTHING;

INSERT INTO honua.sta_datastream (
    id, name, description, observation_type, unit_name, unit_symbol, unit_definition,
    thing_id, sensor_id, observed_property_id)
VALUES (
    1,
    'Demo Air Temperature',
    'Synthetic air-temperature datastream for the SensorThings Phase 1 demo',
    'http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement',
    'degree Celsius', '°C', 'http://unitsofmeasure.org/ucum.html#para-30',
    1, 1, 1)
ON CONFLICT (id) DO NOTHING;

-- Seed a deterministic synthetic series: 48 hourly observations ending now,
-- result = 15 + 10 * sin(hour) so $filter/$orderby are demoable against varied values.
INSERT INTO honua.sta_observation (id, datastream_id, phenomenon_time, result_time, result, feature_of_interest_id)
SELECT
    gs AS id,
    1 AS datastream_id,
    (now() - ((48 - gs) || ' hours')::interval) AS phenomenon_time,
    (now() - ((48 - gs) || ' hours')::interval) AS result_time,
    15.0 + 10.0 * sin(gs::double precision) AS result,
    NULL::bigint AS feature_of_interest_id
FROM generate_series(1, 48) AS gs
ON CONFLICT (id, phenomenon_time) DO NOTHING;
