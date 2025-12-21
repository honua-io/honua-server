-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Attachments table migration
-- Creates attachments table for file attachments associated with geographic features

-- Attachments table - stores metadata for feature attachments
CREATE TABLE honua.attachments (
    id BIGSERIAL PRIMARY KEY,
    feature_id BIGINT NOT NULL,
    layer_id INT NOT NULL,
    filename TEXT NOT NULL,
    content_type TEXT NOT NULL,
    size BIGINT NOT NULL CHECK (size >= 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    storage_path TEXT NOT NULL,
    keywords TEXT
);

-- Indexes for performance
CREATE INDEX idx_attachments_feature_layer ON honua.attachments(layer_id, feature_id);
CREATE INDEX idx_attachments_created_at ON honua.attachments(created_at);
CREATE INDEX idx_attachments_layer_id ON honua.attachments(layer_id);

-- Comments for documentation
COMMENT ON TABLE honua.attachments IS 'File attachments associated with geographic features';
COMMENT ON COLUMN honua.attachments.id IS 'Unique attachment identifier';
COMMENT ON COLUMN honua.attachments.feature_id IS 'Feature identifier that owns this attachment';
COMMENT ON COLUMN honua.attachments.layer_id IS 'Layer identifier containing the feature';
COMMENT ON COLUMN honua.attachments.filename IS 'Original filename of the uploaded file';
COMMENT ON COLUMN honua.attachments.content_type IS 'MIME content type of the file (e.g., image/jpeg, application/pdf)';
COMMENT ON COLUMN honua.attachments.size IS 'File size in bytes';
COMMENT ON COLUMN honua.attachments.created_at IS 'Timestamp when the attachment was uploaded';
COMMENT ON COLUMN honua.attachments.storage_path IS 'Relative path to the stored file on the filesystem';
COMMENT ON COLUMN honua.attachments.keywords IS 'Optional keywords or tags for the attachment';