# Operate Observability Fixtures

Honua Server exposes an explicit Development/Test seed facility for Console
Testcontainers that need live Operate observability data from a real
honua-server plus PostgreSQL runtime.

This is the supported, non-production seed path that satisfies
[honua-server#1229](https://github.com/honua-io/honua-server/issues/1229): it lets
a downstream Console Testcontainers fixture create deterministic **active alert**
and **durable job** records (plus related telemetry/log/event/investigation rows
for cross-surface links) against a real honua-server image without reaching into
PostgreSQL tables. The seeded records are visible only through the same
public/admin observability read APIs Console consumes, and the path is disabled
or fails fast in normal production configuration. This unblocks
[honua-console#24](https://github.com/honua-io/honua-console/issues/24)'s final
live alert/job evidence.

The fixture is disabled by default. When enabled it is rejected outside
`Development` and `Test` environments and requires the PostgreSQL provider; both
checks fail fast at startup (`OptionsValidationException`) rather than serving
traffic. When disabled it registers no endpoint, no hosted service, and no
fixture job/log store, so it adds no production startup or memory cost.

## Enable

Set these values on the server container:

```bash
ASPNETCORE_ENVIRONMENT=Test
OperateObservabilityFixture__Enabled=true
OperateObservabilityFixture__Profile=console-testcontainers-v1
OperateObservabilityFixture__SeedOnStartup=false
```

CI and Testcontainers can instead use the single flat alias
`HONUA_ENABLE_OBSERVABILITY_TEST_SEED` (mirroring `HONUA_DEV_AUTH`). It is
equivalent to the nested `OperateObservabilityFixture__Enabled=true` key and is
the recommended switch for Console's Testcontainers fixture:

```bash
ASPNETCORE_ENVIRONMENT=Test
HONUA_ENABLE_OBSERVABILITY_TEST_SEED=true
```

The flat alias can only enable the feature; it never disables an explicit
`OperateObservabilityFixture__Enabled=true`. Like the nested key, it is rejected
outside `Development`/`Test` (startup fails with `OptionsValidationException`), so
setting it in production does not expose the seed path.

The container must use PostgreSQL. The fixture does not support DuckDB, MySQL,
or MariaDB providers.

`console-testcontainers-v1` is the only supported profile; any other value fails
options validation at startup.

### Seed on startup vs. explicit endpoint

| `SeedOnStartup` | Behavior |
| --- | --- |
| `false` (default) | The fixture registers the seed endpoint only. Tests call it once the server and PostgreSQL are healthy. |
| `true` | A hosted service seeds the profile during startup in addition to exposing the endpoint. |

The explicit endpoint is the preferred deterministic Testcontainers trigger
because it avoids startup readiness races against PostgreSQL. The seed path is
idempotent, so calling the endpoint after a startup seed does not duplicate
rows.

## Seed

Call the explicit admin endpoint after the server and PostgreSQL are healthy:

```http
POST /api/v1/admin/dev/fixtures/operate-observability/console-testcontainers-v1
X-API-Key: <admin-api-key>
```

The endpoint requires admin authorization. A profile other than the configured
`console-testcontainers-v1` returns `404 Not Found` with an admin problem
response. The seed path is idempotent for repeated calls in the same container:
fixture-owned rows are keyed by profile and `serviceId` and are replaced rather
than duplicated, and no user-created data is touched.

## Response contract

The seed call returns `OperateObservabilityFixtureSeedResponse` (camelCase, null
properties omitted). Names, job IDs, dedupe keys, and the investigation ID are
stable; the numeric `zoneId`, `ruleId`, `openAlertEventId`,
`resolvedAlertEventId`, `pinIds`, and `linkIds` are database-generated and are
returned so tests can assert exact rows and links (the numbers below are
illustrative).

```json
{
  "profile": "console-testcontainers-v1",
  "sourceHydrated": true,
  "seededAt": "2026-05-25T12:00:00+00:00",
  "serviceId": "operate-fixture-console",
  "layerId": 1,
  "correlationId": "corr-operate-fixture-001",
  "traceId": "trace-operate-fixture-001",
  "actor": "operator.testcontainers",
  "alerts": {
    "zoneId": 12,
    "zoneName": "Honolulu Harbor Testcontainers",
    "ruleId": 8,
    "ruleName": "Harbor Entry Testcontainers",
    "openAlertEventId": 101,
    "resolvedAlertEventId": 102,
    "dedupeKeys": [
      "console-testcontainers-v1:alert-open",
      "console-testcontainers-v1:alert-resolved"
    ]
  },
  "jobs": [
    {
      "jobId": "operate-fixture-job-succeeded",
      "status": "Succeeded",
      "detailLink": "/api/v1/admin/jobs/operate-fixture-job-succeeded",
      "logsLink": "/api/v1/admin/jobs/operate-fixture-job-succeeded/logs",
      "artifactsLink": "/api/v1/admin/jobs/operate-fixture-job-succeeded/artifacts"
    },
    {
      "jobId": "operate-fixture-job-failed",
      "status": "Failed",
      "detailLink": "/api/v1/admin/jobs/operate-fixture-job-failed",
      "logsLink": "/api/v1/admin/jobs/operate-fixture-job-failed/logs",
      "artifactsLink": "/api/v1/admin/jobs/operate-fixture-job-failed/artifacts"
    }
  ],
  "investigation": {
    "investigationId": "inv-operate-fixture-console",
    "pinIds": [201, 202],
    "linkIds": [301, 302, 303, 304],
    "detailLink": "/api/v1/admin/investigations/inv-operate-fixture-console"
  },
  "links": {
    "events": "/api/v1/admin/observability/events?correlationId=corr-operate-fixture-001",
    "logs": "/api/v1/admin/observability/logs",
    "alerts": "/api/v1/admin/observability/alerts?serviceId=operate-fixture-console",
    "alertRules": "/api/v1/admin/alerts/rules?serviceId=operate-fixture-console",
    "alertZones": "/api/v1/admin/alerts/zones?serviceId=operate-fixture-console",
    "jobs": "/api/v1/admin/jobs?correlationId=corr-operate-fixture-001",
    "investigation": "/api/v1/admin/investigations/inv-operate-fixture-console"
  }
}
```

`sourceHydrated: true` is the marker Console Testcontainers assert to prove the
slice rendered from a live honua-server rather than `InMemory*` server-owned
data. The `links` map returns the exact admin read URLs (already filtered by the
fixture `correlationId`/`serviceId`) that hydrate each Operate surface. The
seeded alert set is one open `Warning` event plus one resolved `Critical` event;
the investigation seeds two pins (open alert, failed job) and four links (open
alert, failed job, release, change-set).

## Seeded surface

The `console-testcontainers-v1` profile seeds the following, each readable
through an existing production admin endpoint:

| Surface | Evidence path (matches a `links` key) |
| --- | --- |
| Unified Operate events | `GET /api/v1/admin/observability/events?correlationId=corr-operate-fixture-001` |
| Recent logs | `GET /api/v1/admin/observability/logs` |
| Active and resolved alerts | `GET /api/v1/admin/observability/alerts?serviceId=operate-fixture-console` |
| Alert rule admin data | `GET /api/v1/admin/alerts/rules?serviceId=operate-fixture-console` |
| Alert zone admin data | `GET /api/v1/admin/alerts/zones?serviceId=operate-fixture-console` |
| Durable jobs | `GET /api/v1/admin/jobs?correlationId=corr-operate-fixture-001` |
| Job detail/log/artifact links | `/api/v1/admin/jobs/operate-fixture-job-failed/**` |
| Investigation pins and links | `GET /api/v1/admin/investigations/inv-operate-fixture-console` |

Fixed identifiers:

| Key | Value |
| --- | --- |
| Service | `operate-fixture-console` |
| Correlation | `corr-operate-fixture-001` |
| Trace | `trace-operate-fixture-001` |
| Actor | `operator.testcontainers` |
| Alert zone name | `Honolulu Harbor Testcontainers` |
| Alert rule name | `Harbor Entry Testcontainers` |
| Succeeded job | `operate-fixture-job-succeeded` |
| Failed job | `operate-fixture-job-failed` |
| Investigation | `inv-operate-fixture-console` |

The alert zone polygon is a small Honolulu Harbor footprint declared in
WGS84/EPSG:4326 and validated through the same geometry path as the production
alert-zone admin API.

## #1229 acceptance criteria

| Criterion | How this path satisfies it |
| --- | --- |
| Start a real container with documented test/dev config and seed deterministic alert + job data | `ASPNETCORE_ENVIRONMENT=Test` + `HONUA_ENABLE_OBSERVABILITY_TEST_SEED=true` (or `OperateObservabilityFixture__Enabled=true`), then `POST /api/v1/admin/dev/fixtures/operate-observability/console-testcontainers-v1` with `X-API-Key`. |
| Seeded records visible through public/admin observability APIs, not private tables | Alerts via `GET /api/v1/admin/observability/alerts?serviceId=operate-fixture-console`; jobs via `GET /api/v1/admin/jobs?correlationId=corr-operate-fixture-001`. The seeder writes through `IAlertEventStore`/`IExecutionJobStore`, the same stores the read APIs use. |
| Seed path disabled/inaccessible in normal production | Disabled by default (endpoint returns `404`); enabling it under a non-Development/Test environment fails startup with `OptionsValidationException`. |
| Documentation names env vars, auth, and expected record IDs/names | This document — see **Enable**, **Seed**, and the **Fixed identifiers** table. |
| Console#24 can replace skipped/partial live alert + job evidence | The `alerts`/`jobs` keys in the response `links` map return the exact filtered read URLs to assert against. |

The deterministic identifiers Console asserts by (auth: `X-API-Key` admin key):

- Active (open) alert: lifecycle `open`, severity `Warning`, `serviceId=operate-fixture-console`, dedupe key `console-testcontainers-v1:alert-open`.
- Resolved alert: lifecycle `resolved`, severity `Critical`, dedupe key `console-testcontainers-v1:alert-resolved`.
- Durable jobs: `operate-fixture-job-succeeded` (Succeeded) and `operate-fixture-job-failed` (Failed).
- Investigation: `inv-operate-fixture-console`.

## Notes

- Durable jobs and logs persist to PostgreSQL fixture tables
  (`honua.operate_fixture_execution_jobs`, `honua.operate_fixture_execution_logs`),
  registered only when the fixture is enabled and no Redis-backed job/log store
  is already present. This satisfies the PostgreSQL-only Testcontainers shape
  without standing up Redis.
- Recent logs are seeded into the process-local `RecentErrorBuffer`, matching the
  production `/observability/logs` source. They are deterministic for a single
  running server instance but are not durable across server restarts; re-seed
  after a restart.
- The endpoint is dev/test-only and is intentionally excluded from the curated
  `docs/developer/api-specs/admin-api.json` SDK-generation snapshot.
