-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Independent, styleId-keyed style catalog (ADR-0048, Phase 2, issue #1389).
--
-- Phase 1 stored style bytes strictly per-layer (honua.layers.maplibre_style,
-- keyed by the integer storage-layer id) so one style could never be shared by
-- many layers. Phase 2 introduces a first-class style store keyed by a stable
-- string styleId, decoupled from any single layer, plus an ordered many-to-many
-- association table so a single style can render many data resources.
--
-- honua.layers.maplibre_style remains the canonical per-layer Phase 1 store and
-- is NOT dropped here: the catalog is additive (per ADR-0048 "trade-offs
-- accepted"), and existing per-layer styles are backfilled into a default style
-- per styled layer so nothing regresses.

CREATE TABLE IF NOT EXISTS honua.styles
(
    style_id            TEXT PRIMARY KEY,
    title               TEXT,
    description         TEXT,
    maplibre_style      JSONB NOT NULL,
    drawing_info        JSONB,
    style_version       INT NOT NULL DEFAULT 1,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revised_by          TEXT,
    change_summary      TEXT,
    CONSTRAINT styles_style_id_not_blank_check
        CHECK (length(btrim(style_id)) > 0),
    CONSTRAINT styles_change_summary_length_check
        CHECK (change_summary IS NULL OR char_length(change_summary) <= 1000)
);

COMMENT ON TABLE honua.styles IS
    'Independent styleId-keyed style catalog (ADR-0048 Phase 2). One style may render many layers.';
COMMENT ON COLUMN honua.styles.style_id IS 'Stable string style identifier (honua://styles/{style_id}).';
COMMENT ON COLUMN honua.styles.maplibre_style IS 'Canonical MapLibre/Mapbox style JSON. Derived encodings (SLD, drawingInfo) are produced on demand.';
COMMENT ON COLUMN honua.styles.drawing_info IS 'Optional cached GeoServices drawingInfo JSON for FeatureServer/MapServer.';
COMMENT ON COLUMN honua.styles.style_version IS 'Author-managed integer style version, incremented on canonical updates.';

-- Ordered many-to-many association: a layer references styles by ordinal
-- (ordinal 0 = primary, matching MetadataV2Resource.StyleResourceIds[0]).
CREATE TABLE IF NOT EXISTS honua.layer_style_refs
(
    layer_id    INT NOT NULL REFERENCES honua.layers (layer_id) ON DELETE CASCADE,
    style_id    TEXT NOT NULL REFERENCES honua.styles (style_id) ON DELETE CASCADE,
    ordinal     INT NOT NULL DEFAULT 0,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (layer_id, style_id)
);

COMMENT ON TABLE honua.layer_style_refs IS
    'Ordered association between layers and catalog styles (ADR-0048 Phase 2). ordinal 0 is the primary style.';

CREATE INDEX IF NOT EXISTS layer_style_refs_layer_idx
    ON honua.layer_style_refs (layer_id, ordinal);
CREATE INDEX IF NOT EXISTS layer_style_refs_style_idx
    ON honua.layer_style_refs (style_id);

-- Backfill: migrate every existing per-layer MapLibre style into a default
-- catalog style (styleId "style-layer-{layer_id}") and a primary association,
-- so the Type=Style graph producer lights up StyleResourceIds with real data
-- without operator action. Idempotent: re-running skips rows already present.
INSERT INTO honua.styles
    (style_id, title, description, maplibre_style, drawing_info, style_version,
     created_at, updated_at, revised_by, change_summary)
SELECT
    'style-layer-' || l.layer_id::text,
    l.layer_name,
    NULL,
    l.maplibre_style,
    l.geoservices_drawing_info,
    GREATEST(COALESCE(l.style_version, 1), 1),
    COALESCE(l.style_revised_at, NOW()),
    COALESCE(l.style_revised_at, NOW()),
    l.style_revised_by,
    l.style_change_summary
FROM honua.layers l
WHERE l.maplibre_style IS NOT NULL
ON CONFLICT (style_id) DO NOTHING;

INSERT INTO honua.layer_style_refs (layer_id, style_id, ordinal)
SELECT l.layer_id, 'style-layer-' || l.layer_id::text, 0
FROM honua.layers l
WHERE l.maplibre_style IS NOT NULL
ON CONFLICT (layer_id, style_id) DO NOTHING;
