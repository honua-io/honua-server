-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Object storage connections grant read-only access to one bucket prefix through a customer role.
-- Each external id is minted by setup, used once by create, and fixed for the life of the connection.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.object_storage_connection_setups (
    external_id TEXT COLLATE "C" PRIMARY KEY,
    minted_by TEXT NOT NULL,
    bucket TEXT COLLATE "C" NOT NULL,
    prefix TEXT COLLATE "C" NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    expires_at TIMESTAMPTZ NOT NULL,
    consumed_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS object_storage_connection_setups_open
    ON $HonuaSchema$.object_storage_connection_setups (minted_by, expires_at) WHERE consumed_at IS NULL;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.object_storage_connections (
    connection_id UUID PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NULL,
    provider TEXT COLLATE "C" NOT NULL DEFAULT 'aws-s3',
    bucket TEXT COLLATE "C" NOT NULL,
    prefix TEXT COLLATE "C" NOT NULL DEFAULT '',
    region TEXT COLLATE "C" NOT NULL,
    role_arn TEXT COLLATE "C" NOT NULL,
    external_id TEXT COLLATE "C" NOT NULL UNIQUE
        REFERENCES $HonuaSchema$.object_storage_connection_setups (external_id),
    created_by TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT object_storage_connections_provider CHECK (provider = 'aws-s3')
);

-- The external id, bucket and prefix are what the customer role trusts; an update may not move them.
CREATE OR REPLACE FUNCTION $HonuaSchema$.keep_object_storage_connection_binding() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.external_id IS DISTINCT FROM OLD.external_id
        OR NEW.bucket IS DISTINCT FROM OLD.bucket
        OR NEW.prefix IS DISTINCT FROM OLD.prefix THEN
        RAISE EXCEPTION 'object storage connection binding is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER object_storage_connections_binding BEFORE UPDATE
    ON $HonuaSchema$.object_storage_connections FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.keep_object_storage_connection_binding();

CREATE OR REPLACE TRIGGER object_storage_connections_generation AFTER INSERT OR UPDATE OR DELETE
    ON $HonuaSchema$.object_storage_connections FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.advance_configuration_generation();
