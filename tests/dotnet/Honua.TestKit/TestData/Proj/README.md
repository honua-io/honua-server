# NADCON execution fixture

`us_noaa_conus.tif` is a 5-by-5 node subset of the public-domain NOAA NAD27-to-NAD83 grid, converted to GeoTIFF by the PROJ project. It surrounds the fixture point (-100,40).

- Source: https://cdn.proj.org/us_noaa_conus.tif
- Provenance and public-domain license: https://github.com/OSGeo/PROJ-data/blob/master/us_noaa/us_noaa_README.txt
- Full source SHA-256: `44611d823c48e5347500ee6afe40ff33d2b88cf817bf59f705ed4a4c3bd687d7` (173029 bytes; retrieved 2026-09-05).
- Subset SHA-256: `1f0f1125af707a6c55dab23a3a21ab4354e100967bf5b34ca670fdcb4f1f65e6` (1720 bytes).

Native rasterio selected the five rows/columns centered on `src.index(-100,40)`, copying both bands unchanged and retaining the point registration, CRS, geotransform, band descriptions, units, and metadata. Independent pyproj evaluation of the full grid and subset agrees within 1e-12 degrees in both directions at the fixture point; Z=12 and M=7 remain unchanged.

The dedicated fixture pins PostGIS 18-3.6 to digest `sha256:60f6ad1d21ea86a67d47780b9a0d1e1d200500f62b19293fa834d0dea80b8677` and isolates `PROJ_DATA`/`PROJ_LIB` in a directory containing only that image's `proj.db` and, for the NADCON profile, this grid. `PROJ_NETWORK=OFF` prevents downloads. The shared database is unchanged; CI's PostGIS 16 image includes a legacy `conus` grid and must not be assumed grid-free.

The default-operation HTTP cases exercise both profiles in both directions at (-100,40), asserting independent reference XY within `2e-9` degrees, Z=12, M=7, and destination WKID. The explicit WKID 1241 case uses the same NADCON profile. Grid-free references use the CONUS Helmert translation (-8,159,175), while grid-backed references use the pinned grid, independently evaluated with pyproj outside PostGIS.

Run `python3 compute-references.py` with pyproj 3.7.2 / PROJ 9.5.1 to reproduce the four XY references. The script isolates the operation database and writable grid cache and disables network access. It never calls Honua or PostGIS and does not regenerate assertions from server output.
