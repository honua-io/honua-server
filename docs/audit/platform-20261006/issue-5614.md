# Issue 5614: local hidden-field recovery

Owner: Mike McDougall <mike@honua.io>. One writer, one issue, one PR.

Current scope was read from issue 5614 on 2026-10-08. No existing PR or remote
recovery branch was found. Recovery began from trunk
`5d2ffbb7cd34739b76f6326db5991b01ae2079a4`. The parked cloud packet was retained;
its READY status and historical test statements were not used as evidence.

## Work in progress

| Finding | Outcome | Evidence or next action |
| --- | --- | --- |
| SRV-OGC-007 | not attempted | WFS baseline requests have reproduced hidden output and accepted hidden PROPERTYNAME references; the run is still completing. OGC baseline execution is pending. Production code is unchanged. |
| SRV-GRPC-003 | not attempted | New unary and streaming hidden-field integration regressions are awaiting local baseline execution. Production code is unchanged. |

The tests use `WebAppFixture` and `PostgresFixture` (Testcontainers image
`postgis/postgis:18-3.6`), an isolated schema and the real seeded feature rows.
The GML streaming regression inserts 300 additional rows and requires exactly
300 response members. All build/test commands use PATH's slotted dotnet shim
with the four-slot admission and MSBuild CPU cap. Only observed WFS baseline failures have been recorded so far; final
counts and OGC/gRPC runtime results remain pending. An initial compile failure in a new test namespace import was
corrected before runtime diagnosis.

Remaining steps: baseline reproduction, supported adapter corrections,
focused verification, project-scoped formatting, latest-trunk reconciliation,
one self-review and independent review, then normal hosted PR gate admission.
