-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Add style revision metadata for the canonical MapLibre style engine.
-- These columns supplement the existing style_version counter so the
-- Admin UI and downstream tooling can display authorship, change summaries,
-- and revision timestamps without inspecting JSONB diffs.

ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS style_revised_at TIMESTAMPTZ;

ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS style_revised_by TEXT;

ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS style_change_summary TEXT;

ALTER TABLE honua.layers
    DROP CONSTRAINT IF EXISTS layers_style_change_summary_length_check;

ALTER TABLE honua.layers
    ADD CONSTRAINT layers_style_change_summary_length_check
        CHECK (style_change_summary IS NULL OR char_length(style_change_summary) <= 1000);

-- Align style_version with the canonical "row updated only by PUT" contract:
-- a layer that has never received a PUT must report styleVersion 0, and the
-- first PUT must land as revision 1.  Migration 009 originally seeded
-- style_version with DEFAULT 1, so freshly published rows would GET as
-- styleVersion 1 even before any operator update -- making the first PUT
-- increment to 2.  Switch the default to 0 and backfill rows that look like
-- they were never styled (no maplibre_style and no revision timestamp).
-- Rows that have either column populated were already touched by a real PUT
-- and keep their existing version.
ALTER TABLE honua.layers
    ALTER COLUMN style_version SET DEFAULT 0;

UPDATE honua.layers
SET style_version = 0
WHERE maplibre_style IS NULL
  AND style_revised_at IS NULL;

COMMENT ON COLUMN honua.layers.style_revised_at IS 'UTC timestamp of last canonical style update';
COMMENT ON COLUMN honua.layers.style_revised_by IS 'Author or source identifier of last style update';
COMMENT ON COLUMN honua.layers.style_change_summary IS 'Operator-supplied description of the change (max 1000 chars)';
