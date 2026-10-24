-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Persist operator-managed coded-value domain metadata for published fields.
-- Field aliases continue to use honua.layer_fields.description, which already
-- hydrates into FieldDefinition.DisplayName across protocol metadata surfaces.

ALTER TABLE honua.layer_fields
    ADD COLUMN IF NOT EXISTS domain JSONB;

ALTER TABLE honua.layer_fields
    DROP CONSTRAINT IF EXISTS layer_fields_domain_object_check;

ALTER TABLE honua.layer_fields
    ADD CONSTRAINT layer_fields_domain_object_check
        CHECK (domain IS NULL OR jsonb_typeof(domain) = 'object');

COMMENT ON COLUMN honua.layer_fields.domain IS 'Operator-managed field domain metadata such as coded-value domains';
