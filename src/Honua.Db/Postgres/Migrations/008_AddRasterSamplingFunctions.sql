-- Additive sampler functions. Each body is bound from the same C# definition the server ships.
-- SET search_path stops the planner from inlining an unused fallback into the tile query.
-- That is a planning boundary, not a JIT-policy change. No raster table or tile payload changes.

CREATE SCHEMA IF NOT EXISTS honua_raster;
GRANT USAGE ON SCHEMA honua_raster TO PUBLIC;

CREATE OR REPLACE FUNCTION honua_raster.native_window_v1(p_raster raster, p_grid raster)
RETURNS raster
LANGUAGE sql
STABLE
SECURITY INVOKER
SET search_path = pg_catalog, public
AS $$
SELECT $HonuaRasterNativeWindowV1$;
$$;

CREATE OR REPLACE FUNCTION honua_raster.native_window_coverage_v1(p_raster raster, p_grid raster)
RETURNS raster
LANGUAGE sql
STABLE
SECURITY INVOKER
SET search_path = pg_catalog, public
AS $$
SELECT $HonuaRasterNativeWindowCoverageV1$;
$$;

CREATE OR REPLACE FUNCTION honua_raster.separable_nearest_v1(p_raster raster, p_grid raster)
RETURNS raster
LANGUAGE sql
STABLE
SECURITY INVOKER
SET search_path = pg_catalog, public
AS $$
SELECT $HonuaRasterSeparableNearestV1$;
$$;

CREATE OR REPLACE FUNCTION honua_raster.separable_nearest_coverage_v1(p_raster raster, p_grid raster)
RETURNS raster
LANGUAGE sql
STABLE
SECURITY INVOKER
SET search_path = pg_catalog, public
AS $$
SELECT $HonuaRasterSeparableNearestCoverageV1$;
$$;

CREATE OR REPLACE FUNCTION honua_raster.general_nearest_v1(p_raster raster, p_grid raster)
RETURNS raster
LANGUAGE sql
STABLE
SECURITY INVOKER
SET search_path = pg_catalog, public
AS $$
SELECT $HonuaRasterGeneralNearestV1$;
$$;

CREATE OR REPLACE FUNCTION honua_raster.general_nearest_coverage_v1(p_raster raster, p_grid raster)
RETURNS raster
LANGUAGE sql
STABLE
SECURITY INVOKER
SET search_path = pg_catalog, public
AS $$
SELECT $HonuaRasterGeneralNearestCoverageV1$;
$$;

-- Callers already authorized to read the raster arguments. These functions read no tables.
GRANT EXECUTE ON FUNCTION honua_raster.native_window_v1(raster, raster) TO PUBLIC;
GRANT EXECUTE ON FUNCTION honua_raster.native_window_coverage_v1(raster, raster) TO PUBLIC;
GRANT EXECUTE ON FUNCTION honua_raster.separable_nearest_v1(raster, raster) TO PUBLIC;
GRANT EXECUTE ON FUNCTION honua_raster.separable_nearest_coverage_v1(raster, raster) TO PUBLIC;
GRANT EXECUTE ON FUNCTION honua_raster.general_nearest_v1(raster, raster) TO PUBLIC;
GRANT EXECUTE ON FUNCTION honua_raster.general_nearest_coverage_v1(raster, raster) TO PUBLIC;
