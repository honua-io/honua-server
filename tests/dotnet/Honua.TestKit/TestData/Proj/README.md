# NADCON execution fixture

`us_noaa_conus.tif` is a 5-by-5 node subset of the public-domain NOAA NAD27-to-NAD83 grid, converted to GeoTIFF by the PROJ project. It surrounds the fixture point (-100,40).

- Source: https://cdn.proj.org/us_noaa_conus.tif
- Provenance and public-domain license: https://github.com/OSGeo/PROJ-data/blob/master/us_noaa/us_noaa_README.txt
- Full source SHA-256: `44611d823c48e5347500ee6afe40ff33d2b88cf817bf59f705ed4a4c3bd687d7` (173029 bytes; retrieved 2026-09-05).
- Subset SHA-256: `1f0f1125af707a6c55dab23a3a21ab4354e100967bf5b34ca670fdcb4f1f65e6` (1720 bytes).

Native rasterio selected the five rows/columns centered on `src.index(-100,40)`, copying both bands unchanged and retaining the point registration, CRS, geotransform, band descriptions, units, and metadata. Independent pyproj evaluation of the full grid and subset agrees within 1e-12 degrees in both directions at the fixture point; Z=12 and M=7 remain unchanged.

The dedicated fixture mounts this pinned file into its own PostGIS container. Tests need no grid download, and the shared base-image fixture retains its grid-free environment.
