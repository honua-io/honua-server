# Issue 5614: local hidden-field recovery

Owner: Mike McDougall <mike@honua.io>. One writer, one issue, one PR.

Current scope was read from issue 5614 on 2026-10-08. No existing PR or remote
recovery branch was found. Recovery began from trunk
`5d2ffbb7cd34739b76f6326db5991b01ae2079a4`. The parked cloud packet was retained;
its READY status and historical test statements were not used as evidence.

## Work in progress

| Finding | Outcome | Evidence or next action |
| --- | --- | --- |
| SRV-OGC-007 | not attempted | WFS baseline completed: 7 failed, 1 passed, 0 skipped (8 cases). GML/CSV, DescribeFeatureType, PROPERTYNAME, SORTBY, GetPropertyValue and FES failures support a small WFS correction, now applied but awaiting verification. OGC runtime diagnosis remains queued. |
| SRV-GRPC-003 | not attempted | New unary and streaming hidden-field integration regressions are awaiting local baseline execution. Production code is unchanged. |

The tests use `WebAppFixture` and `PostgresFixture` (Testcontainers image
`postgis/postgis:18-3.6`), an isolated schema and the real seeded feature rows.
The GML streaming regression inserts 300 additional rows and requires exactly
300 response members. All build/test commands use PATH's slotted dotnet shim
with the four-slot admission and MSBuild CPU cap. The completed WFS baseline is preserved in `5614-evidence/wfs-red.trx` and
`5614-wfs-red.log` under the local workspace. OGC/gRPC runtime results remain
pending. The queued baseline commands compile their new test assemblies with
`-p:BuildProjectReferences=false` against already built, unchanged protocol
dependencies; production verification will build the changed dependencies. An initial compile failure in a new test namespace import was
corrected before runtime diagnosis.

Remaining steps: baseline reproduction, supported adapter corrections,
focused verification, project-scoped formatting, latest-trunk reconciliation,
one self-review and independent review, then normal hosted PR gate admission.

## Completed baseline qualification

- WFS GML and CSV GetFeature: reproduced; hidden `category` is present.
- WFS DescribeFeatureType: reproduced; hidden `category` is advertised.
- WFS PROPERTYNAME, SORTBY, GetPropertyValue and FES ValueReference: reproduced;
  requests return HTTP 200 instead of rejecting the hidden field. The
  PROPERTYNAME response includes `<honua:category>test</honua:category>`.
- Already fixed on starting trunk: WFS GeoJSON output passes the hidden-field
  regression through the existing GeoJSON builder.
- Not reproduced: none of the completed WFS checks.
- Not attempted: OGC and gRPC runtime qualification, correction verification,
  formatting and review/publication remain pending build-slot admission.

The four shared slots were confirmed unavailable on 2026-10-08. Active holders
were the release test job and other server test jobs. Their holds were retained.
The WFS source correction is unfinished until its focused tests pass; this
checkpoint is not delivery and does not close issue 5614.
