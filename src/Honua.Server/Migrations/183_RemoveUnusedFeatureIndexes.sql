-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- honua:compatibility-review reason=removes only optional feature indexes; reader SQL and constraints are unchanged
DROP INDEX IF EXISTS public.idx_features_attributes_gin;
DROP INDEX IF EXISTS public.idx_features_attributes_keys;
DROP INDEX IF EXISTS public.idx_features_geometry_nn;
DROP INDEX IF EXISTS public.idx_features_geometry_3d;
DROP INDEX IF EXISTS public.idx_features_envelope;
DROP INDEX IF EXISTS public.idx_features_attr_dates;
DROP INDEX IF EXISTS public.idx_features_attr_timestamps;
DROP INDEX IF EXISTS public.idx_features_temporal_attrs;
