-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 037_CreateOpenDataPublicationStore.sql
-- Dedicated server-owned store for Console open-data page and STAC publication state (#1214).

CREATE SCHEMA IF NOT EXISTS honua;

CREATE TABLE IF NOT EXISTS honua.open_data_pages (
    item_id      TEXT        NOT NULL PRIMARY KEY,
    is_published BOOLEAN    NOT NULL DEFAULT FALSE,
    updated_at   TIMESTAMPTZ NOT NULL,
    updated_by   TEXT,
    page_json    JSONB       NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_open_data_pages_published_updated
    ON honua.open_data_pages(is_published, updated_at DESC, item_id);

CREATE TABLE IF NOT EXISTS honua.open_data_stac_publications (
    collection_id    TEXT        NOT NULL PRIMARY KEY,
    item_id          TEXT        NOT NULL,
    status           TEXT        NOT NULL,
    updated_at       TIMESTAMPTZ NOT NULL,
    publication_json JSONB       NOT NULL,
    CONSTRAINT open_data_stac_publications_status
        CHECK (status IN ('Published', 'Unpublished'))
);

CREATE INDEX IF NOT EXISTS idx_open_data_stac_publications_item_updated
    ON honua.open_data_stac_publications(item_id, updated_at DESC, collection_id);

CREATE INDEX IF NOT EXISTS idx_open_data_stac_publications_status_updated
    ON honua.open_data_stac_publications(status, updated_at DESC, collection_id);
