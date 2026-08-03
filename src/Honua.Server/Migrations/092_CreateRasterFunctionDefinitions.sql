-- Tenant-scoped named raster-function definitions are append-only. The mutable head row
-- serializes version creation; every version row is protected against update and delete.
CREATE TABLE IF NOT EXISTS honua.raster_function_definitions (
    tenant_id VARCHAR(128) NOT NULL,
    function_name VARCHAR(128) NOT NULL,
    current_version INTEGER NOT NULL DEFAULT 0,
    created_by VARCHAR(256),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, function_name),
    CONSTRAINT raster_function_definitions_tenant_check
        CHECK (tenant_id = btrim(tenant_id) AND char_length(tenant_id) > 0),
    CONSTRAINT raster_function_definitions_name_check
        CHECK (function_name = btrim(function_name) AND char_length(function_name) > 0),
    CONSTRAINT raster_function_definitions_version_check CHECK (current_version >= 0),
    CONSTRAINT raster_function_definitions_created_by_check
        CHECK (created_by IS NULL OR (created_by = btrim(created_by) AND char_length(created_by) <= 256))
);

CREATE TABLE IF NOT EXISTS honua.raster_function_definition_versions (
    tenant_id VARCHAR(128) NOT NULL,
    function_name VARCHAR(128) NOT NULL,
    version INTEGER NOT NULL,
    definition_hash VARCHAR(64) NOT NULL,
    contract_version INTEGER NOT NULL,
    definition_body JSONB NOT NULL,
    expected_previous_version INTEGER NOT NULL,
    idempotency_key VARCHAR(128) NOT NULL,
    created_by VARCHAR(256),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, function_name, version),
    CONSTRAINT raster_function_definition_versions_parent_fk
        FOREIGN KEY (tenant_id, function_name)
        REFERENCES honua.raster_function_definitions (tenant_id, function_name),
    CONSTRAINT raster_function_definition_versions_idempotency_unique
        UNIQUE (tenant_id, function_name, idempotency_key),
    CONSTRAINT raster_function_definition_versions_sequence_check
        CHECK (version > 0 AND expected_previous_version = version - 1),
    CONSTRAINT raster_function_definition_versions_hash_check
        CHECK (definition_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT raster_function_definition_versions_contract_check CHECK (contract_version > 0),
    CONSTRAINT raster_function_definition_versions_idempotency_check
        CHECK (idempotency_key = btrim(idempotency_key) AND char_length(idempotency_key) > 0),
    CONSTRAINT raster_function_definition_versions_created_by_check
        CHECK (created_by IS NULL OR (created_by = btrim(created_by) AND char_length(created_by) <= 256))
);

CREATE OR REPLACE FUNCTION honua.reject_raster_function_definition_version_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'raster function definition versions are immutable'
        USING ERRCODE = '55000';
END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_trigger
        WHERE tgname = 'trg_raster_function_definition_versions_immutable'
          AND tgrelid = 'honua.raster_function_definition_versions'::regclass
    ) THEN
        CREATE TRIGGER trg_raster_function_definition_versions_immutable
            BEFORE UPDATE OR DELETE ON honua.raster_function_definition_versions
            FOR EACH ROW
            EXECUTE FUNCTION honua.reject_raster_function_definition_version_mutation();
    END IF;
END;
$$;

-- No seed definitions are intentional: persistence alone does not advertise or enable execution.
