-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Add optional secure connection association to services

ALTER TABLE IF EXISTS honua.services
    ADD COLUMN IF NOT EXISTS connection_id UUID;

CREATE INDEX IF NOT EXISTS idx_services_connection_id
    ON honua.services(connection_id);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = 'honua'
          AND table_name = 'data_connections'
    ) THEN
        IF NOT EXISTS (
            SELECT 1
            FROM pg_constraint
            WHERE conname = 'fk_services_connection_id'
        ) THEN
            ALTER TABLE honua.services
                ADD CONSTRAINT fk_services_connection_id
                FOREIGN KEY (connection_id)
                REFERENCES honua.data_connections(connection_id)
                ON DELETE RESTRICT;
        END IF;
    END IF;
END $$;
