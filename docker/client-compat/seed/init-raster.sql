-- Install raster support before Honua applies its provider migrations.
CREATE EXTENSION IF NOT EXISTS postgis_raster;
