# COPC source metadata fixture

`source-format6.copc.laz` is the existing three-point synthetic PDAL writers.copc output prepared for the CNG conversion audit, reused here for header-only source-registration tests. SHA-256: `ca2167bd58fce354f2ebdc5bc0e48282ab88312900796b0e436ab57ec1fdfa4d` (1,438 bytes). Its source coordinates are synthetic, CRS EPSG:4979, point format6, scales1e-7/1e-7/0.001. The header test does not claim chunk, CRS or hierarchy validation. Existing uncompressed LAS decode tests remain authoritative for the decoder guard.

Producer pipeline: readers.text over three synthetic XYZ records, then writers.copc with a_srs EPSG:4979 and automatic offsets. Source points: (10.0000001,20.0000002,30.125), (10.0000013,20.0000017,31.375), (9.9999989,19.9999991,29.875). Exact producer image/version receipts remain in the existing CNG audit evidence; no new production or client qualification is claimed by copying this fixture.
