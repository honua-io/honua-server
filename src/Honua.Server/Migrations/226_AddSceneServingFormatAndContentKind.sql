-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Separate canonical serving format (3d-tiles, i3s, copc, quantized-mesh, terrain-rgb)
-- from semantic content kind (mesh, buildings, point-cloud, terrain, vector-3d, unclassified).
-- Legacy rows keep NULL and project safely via SceneServingProjection.

ALTER TABLE honua.scene_datasets
    ADD COLUMN IF NOT EXISTS serving_format VARCHAR(32),
    ADD COLUMN IF NOT EXISTS content_kind VARCHAR(32);

COMMENT ON COLUMN honua.scene_datasets.serving_format IS
    'Optional canonical serving representation (3d-tiles, i3s, copc, quantized-mesh, terrain-rgb).';
COMMENT ON COLUMN honua.scene_datasets.content_kind IS
    'Optional semantic content kind (mesh, buildings, point-cloud, terrain, vector-3d, unclassified).';
