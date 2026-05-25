# Operate Observability Fixtures

Honua Server exposes an explicit Development/Test seed facility for Console
Testcontainers that need live Operate observability data from a real
honua-server plus PostgreSQL runtime.

The fixture is disabled by default and is rejected outside `Development` and
`Test` environments.

## Enable

Set these values on the server container:

```bash
ASPNETCORE_ENVIRONMENT=Test
OperateObservabilityFixture__Enabled=true
OperateObservabilityFixture__Profile=console-testcontainers-v1
OperateObservabilityFixture__SeedOnStartup=false
```

The container must use PostgreSQL. The fixture does not support DuckDB, MySQL,
or MariaDB providers.

## Seed

Call the explicit admin endpoint after the server and PostgreSQL are healthy:

```http
POST /api/v1/admin/dev/fixtures/operate-observability/console-testcontainers-v1
X-API-Key: <admin-api-key>
```

The response includes `sourceHydrated: true`, stable keys, generated alert
zone/rule/event IDs, fixed job IDs, the fixed investigation ID, and relative
links back to the existing Console read APIs.

## Seeded Surface

The `console-testcontainers-v1` profile seeds:

| Surface | Evidence path |
| --- | --- |
| Unified Operate events | `GET /api/v1/admin/observability/events?correlationId=corr-operate-fixture-001` |
| Recent logs | `GET /api/v1/admin/observability/logs` |
| Active and resolved alerts | `GET /api/v1/admin/observability/alerts?serviceId=operate-fixture-console` |
| Alert zone/rule admin data | `GET /api/v1/admin/alerts/zones`, `GET /api/v1/admin/alerts/rules` |
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
| Succeeded job | `operate-fixture-job-succeeded` |
| Failed job | `operate-fixture-job-failed` |
| Investigation | `inv-operate-fixture-console` |

The seed path is idempotent for repeated calls in the same container.
