-- Metadata v2 snapshot storage. The canonical document is stored as JSONB;
-- sidecar tables provide indexed lookup for admin queries that do not need
-- to load the full graph.

CREATE TABLE IF NOT EXISTS metadata_v2_snapshots (
    environment       TEXT          NOT NULL,
    revision          BIGINT        NOT NULL,
    schema_version    TEXT          NOT NULL,
    api_version       TEXT          NOT NULL,
    document          JSONB         NOT NULL,
    etag              TEXT          NOT NULL,
    generated_at      TIMESTAMPTZ   NOT NULL,
    created_at        TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    PRIMARY KEY (environment, revision)
);

-- One row per environment pointing to the active revision.
CREATE TABLE IF NOT EXISTS metadata_v2_current (
    environment       TEXT          NOT NULL PRIMARY KEY,
    revision          BIGINT        NOT NULL,
    etag              TEXT          NOT NULL,
    activated_at      TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    FOREIGN KEY (environment, revision)
        REFERENCES metadata_v2_snapshots(environment, revision)
        ON DELETE RESTRICT
);

-- Sidecar lookup tables. Denormalized from the snapshot document, scoped to
-- (environment, revision) so they roll forward with each snapshot.

CREATE TABLE IF NOT EXISTS metadata_v2_resources_idx (
    environment       TEXT          NOT NULL,
    revision          BIGINT        NOT NULL,
    resource_id       TEXT          NOT NULL,
    name              TEXT          NOT NULL,
    namespace         TEXT          NULL,
    type              TEXT          NOT NULL,
    primary_storage_binding_id TEXT NULL,
    PRIMARY KEY (environment, revision, resource_id),
    FOREIGN KEY (environment, revision)
        REFERENCES metadata_v2_snapshots(environment, revision)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_metadata_v2_resources_name
    ON metadata_v2_resources_idx (environment, revision, name);

CREATE TABLE IF NOT EXISTS metadata_v2_services_idx (
    environment       TEXT          NOT NULL,
    revision          BIGINT        NOT NULL,
    service_id        TEXT          NOT NULL,
    name              TEXT          NOT NULL,
    service_type      TEXT          NOT NULL,
    route             TEXT          NULL,
    PRIMARY KEY (environment, revision, service_id),
    FOREIGN KEY (environment, revision)
        REFERENCES metadata_v2_snapshots(environment, revision)
        ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_metadata_v2_services_name
    ON metadata_v2_services_idx (environment, revision, lower(name));

CREATE TABLE IF NOT EXISTS metadata_v2_publications_idx (
    environment       TEXT          NOT NULL,
    revision          BIGINT        NOT NULL,
    publication_id    TEXT          NOT NULL,
    service_id        TEXT          NOT NULL,
    resource_id       TEXT          NOT NULL,
    storage_binding_id TEXT         NULL,
    publication_type  TEXT          NOT NULL,
    path              TEXT          NULL,
    layer_index       INT           NULL,
    service_local_id  TEXT          NULL,
    PRIMARY KEY (environment, revision, publication_id),
    FOREIGN KEY (environment, revision)
        REFERENCES metadata_v2_snapshots(environment, revision)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_metadata_v2_publications_service
    ON metadata_v2_publications_idx (environment, revision, service_id);

CREATE INDEX IF NOT EXISTS idx_metadata_v2_publications_resource
    ON metadata_v2_publications_idx (environment, revision, resource_id);

CREATE TABLE IF NOT EXISTS metadata_v2_storage_bindings_idx (
    environment       TEXT          NOT NULL,
    revision          BIGINT        NOT NULL,
    storage_binding_id TEXT         NOT NULL,
    resource_id       TEXT          NOT NULL,
    connection_id     TEXT          NULL,
    storage_type      TEXT          NOT NULL,
    locator           TEXT          NOT NULL,
    PRIMARY KEY (environment, revision, storage_binding_id),
    FOREIGN KEY (environment, revision)
        REFERENCES metadata_v2_snapshots(environment, revision)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_metadata_v2_storage_bindings_resource
    ON metadata_v2_storage_bindings_idx (environment, revision, resource_id);

CREATE TABLE IF NOT EXISTS metadata_v2_connections_idx (
    environment       TEXT          NOT NULL,
    revision          BIGINT        NOT NULL,
    connection_id     TEXT          NOT NULL,
    name              TEXT          NOT NULL,
    type              TEXT          NOT NULL,
    provider          TEXT          NULL,
    PRIMARY KEY (environment, revision, connection_id),
    FOREIGN KEY (environment, revision)
        REFERENCES metadata_v2_snapshots(environment, revision)
        ON DELETE CASCADE
);
