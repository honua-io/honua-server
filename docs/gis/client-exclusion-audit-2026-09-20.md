# Client exclusion audit — September 20, 2026

Historical stage: see the [deeper exclusion follow-up](client-exclusion-followup-2026-09-20.md) for current totals and additional native paths.

Follow-up: the [Python lane report](python-lane-status-2026-09-20.md) resolves six
of these audited exclusions with native SDK receipts. The counts and observations
below describe the initial audit; the generated checklist carries current totals.

The four-lane checklist previously closed 167 cells as exclusions. Review found
that several reasons establish only an incomplete probe or an unsuitable fixture,
not the absence of a client capability. The current projection reopens 85 cells
as `blocked` for exclusion-evidence review. This does not assert that all 85 are
supported. It means the recorded reason cannot yet justify removing them from
open work. Every reopened cell retains its original state and citation in
`previous_exclusion`; historical runs and receipts are unchanged.

The denominator stays at 94 operations / 376 cells. The 153 recorded passes and
four existing failures are unchanged. There are now 85 review-blocked cells and
52 not-started cells: 235 closed, 141 open, with 82 remaining exclusions. Remaining
exclusions are not automatically endorsed by this first audit.

| Reopened claim | Cells | Why the exclusion is insufficient |
|---|---:|---|
| Unnamed missing QGIS provider | 30 | The citation supplies neither the provider name nor a receipt. Shared GDAL/OGR drivers must be considered; the installed GDAL exposes OGCAPI. |
| QGIS WCS 2.0.1 excluded using the dedicated WCS provider | 6 | The stock GDAL provider successfully loads WCS 2.0.1 and reads a pixel; the dedicated provider does not define the entire client surface. |
| Pro WCS 1.0 superseded by default negotiation | 3 | A default preference for 2.0.1 does not establish that explicitly selecting 1.0.0 is impossible. |
| Pro OGC API Tiles vector-only fixture | 2 | Pro documents map tiles support. Missing map tiles in a fixture is a setup or server metadata problem to investigate. |
| Pro OData excluded using an OGC classic service list | 2 | OData is not an OGC classic service; the citation cannot establish absence. |

## Evidence identifiers

The checks behind this audit are not restated here (ruling R30). They were
setup and SDK diagnostics, not native UI receipts and not new certification
passes.

- GeoServicesPython build retained with this audit: 3.7.1 build 1901. Pro executable retained
  as 3.7.1.1904. Both stay historical identities, not a new certificate.
- WCS diagnostic on 2026-09-20 about 04:29 UTC: `ERROR 999999`. Not a protocol
  pass and not a no-client exclusion.
- QGIS evidence: `honua-client-compat/evidence/exclusion-audit-qgis-20260920-a/observations.json`
  from `honua-client-compat/scripts/probe-qgis-excluded-raster-paths.py`.
  Server image `sha256:f2e3ee8d7c3975efec1fe84fc9778a119dad77e7af55af8ff9caf3bd0123b4cf`.
- Server licence status and client-seat configuration are not recorded here.

Validation at the time: 152 Python certification tests passed, including two
exclusion regressions. Checklist validation passed with the unchanged 376-cell
denominator.
