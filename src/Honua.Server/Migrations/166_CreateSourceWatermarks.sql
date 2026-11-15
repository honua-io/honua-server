-- Source progress is retained until explicitly cleared by its pipeline.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.source_watermarks (
    pipeline_id TEXT COLLATE "C" NOT NULL,
    source_id TEXT COLLATE "C" NOT NULL,
    record JSONB NOT NULL,
    -- PostgreSQL timestamps have microsecond precision; source progress can differ by one .NET tick.
    timestamp_ticks BIGINT,
    PRIMARY KEY (pipeline_id, source_id)
);
