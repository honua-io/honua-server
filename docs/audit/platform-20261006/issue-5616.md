# Issue #5616 — raster output size caps

Re-verified against current trunk `5d2ffbb7cd` on 2026-10-08. Both findings
still apply: OGC API Coverages gates native clip validation on `subset`, and
WCS 2.0 resolves scaling/native grids without a shared output-size check.

| Finding id | Outcome | Evidence |
| --- | --- | --- |
| SRV-OGC-002 | not attempted | Regression `Coverage_SRV_OGC_002_OversizeNativeBbox_ReturnsBadRequestBeforeExport` authored; pre-fix execution pending. |
| SRV-OGC-004 | not attempted | Regression `Wcs_GetCoverage_SRV_OGC_004_OversizeOutput_ReturnsExceptionBeforeExport` authored; pre-fix execution pending. |
