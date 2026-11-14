-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Native descriptors preserve keyword boundaries and verified content digests.
-- Physical attachment tables can live outside the metadata schema. The provider
-- binds every read to the resolved physical authority, parent and storage path;
-- an older descriptor must never describe bytes replaced through another API.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.attachment_native_metadata (
    attachment_authority TEXT COLLATE "C" NOT NULL CHECK (octet_length(attachment_authority) BETWEEN 1 AND 512),
    attachment_id BIGINT NOT NULL CHECK (attachment_id > 0),
    layer_id INTEGER NOT NULL CHECK (layer_id >= 0),
    feature_id BIGINT NOT NULL,
    storage_path TEXT COLLATE "C" NOT NULL CHECK (octet_length(storage_path) > 0),
    keywords TEXT[] NOT NULL DEFAULT '{}' CHECK (
        array_ndims(keywords) = 1 AND array_position(keywords, NULL) IS NULL),
    content_sha256 BYTEA NOT NULL CHECK (octet_length(content_sha256) = 32),
    global_id UUID NOT NULL DEFAULT gen_random_uuid(),
    PRIMARY KEY (attachment_authority, attachment_id),
    UNIQUE (attachment_authority, global_id)
);

CREATE INDEX IF NOT EXISTS idx_attachment_native_metadata_parent
    ON $HonuaSchema$.attachment_native_metadata (attachment_authority, layer_id, feature_id);
