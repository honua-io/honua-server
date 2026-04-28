-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Secure Connection Registry Migration
-- Creates encrypted credential storage with optional secret manager integration

-- Data connection registry - stores encrypted connection metadata
CREATE TABLE IF NOT EXISTS honua.data_connections (
    connection_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name VARCHAR(64) NOT NULL UNIQUE,
    description TEXT,

    -- Connection metadata (unencrypted)
    host VARCHAR(255) NOT NULL,
    port INT NOT NULL DEFAULT 5432,
    database_name VARCHAR(64) NOT NULL,
    username VARCHAR(64) NOT NULL,
    provider_name TEXT NOT NULL DEFAULT 'postgis',

    -- Security settings
    ssl_required BOOLEAN NOT NULL DEFAULT TRUE,
    ssl_mode VARCHAR(16) NOT NULL DEFAULT 'require', -- disable, allow, prefer, require, verify-ca, verify-full

    -- Encrypted credentials (AES-GCM encrypted connection string)
    connection_string_encrypted BYTEA,
    encryption_key_version INT NOT NULL DEFAULT 1,

    -- Optional secret manager reference (alternative to encrypted storage)
    secret_ref VARCHAR(255), -- Reference to external secret (e.g., "aws:secretsmanager:prod-db-creds")
    secret_type VARCHAR(32), -- aws-secrets-manager, azure-key-vault, hashicorp-vault, etc.

    -- Metadata
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by VARCHAR(64) NOT NULL DEFAULT 'system',

    -- Status and health
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    last_health_check TIMESTAMPTZ,
    health_status VARCHAR(16) DEFAULT 'unknown', -- healthy, unhealthy, unknown

    -- Constraints
    CONSTRAINT chk_secret_or_encrypted CHECK (
        (connection_string_encrypted IS NOT NULL AND secret_ref IS NULL) OR
        (connection_string_encrypted IS NULL AND secret_ref IS NOT NULL)
    ),
    CONSTRAINT chk_ssl_mode CHECK (ssl_mode IN ('disable', 'allow', 'prefer', 'require', 'verify-ca', 'verify-full')),
    CONSTRAINT chk_health_status CHECK (health_status IN ('healthy', 'unhealthy', 'unknown'))
);

-- Encryption key versions for key rotation support
CREATE TABLE IF NOT EXISTS honua.encryption_keys (
    key_version INT PRIMARY KEY,
    key_hash BYTEA NOT NULL, -- SHA-256 hash of the key for verification (not the key itself)
    algorithm VARCHAR(32) NOT NULL DEFAULT 'AES-256-GCM',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by VARCHAR(64) NOT NULL DEFAULT 'system',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    retired_at TIMESTAMPTZ,

    -- Only one active key at a time
    CONSTRAINT chk_algorithm CHECK (algorithm IN ('AES-256-GCM'))
);

-- Indexes for performance
CREATE INDEX IF NOT EXISTS idx_data_connections_name ON honua.data_connections(name);
CREATE INDEX IF NOT EXISTS idx_data_connections_host ON honua.data_connections(host, port);
CREATE INDEX IF NOT EXISTS idx_data_connections_active ON honua.data_connections(is_active) WHERE is_active = TRUE;
CREATE INDEX IF NOT EXISTS idx_data_connections_secret_ref ON honua.data_connections(secret_ref) WHERE secret_ref IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_encryption_keys_active ON honua.encryption_keys(is_active, key_version) WHERE is_active = TRUE;

-- Insert initial encryption key version (placeholder - actual key management handled by application)
INSERT INTO honua.encryption_keys (key_version, key_hash, created_by)
VALUES (1, '\x0000000000000000000000000000000000000000000000000000000000000000', 'migration')
ON CONFLICT DO NOTHING;

-- Comments for documentation
COMMENT ON TABLE honua.data_connections IS 'Secure registry of database connection configurations with encrypted credentials';
COMMENT ON TABLE honua.encryption_keys IS 'Encryption key versions for rotating credentials security';

COMMENT ON COLUMN honua.data_connections.provider_name IS 'Canonical provider engine used to resolve feature-store implementation, e.g. postgis, postgresql, sqlserver, mysql, duckdb';
COMMENT ON COLUMN honua.data_connections.connection_string_encrypted IS 'AES-GCM encrypted PostgreSQL connection string';
COMMENT ON COLUMN honua.data_connections.secret_ref IS 'Reference to external secret manager (alternative to encrypted storage)';
COMMENT ON COLUMN honua.data_connections.ssl_required IS 'Enforce SSL/TLS connections for security';
COMMENT ON COLUMN honua.encryption_keys.key_hash IS 'SHA-256 hash of encryption key for verification (not the actual key)';
