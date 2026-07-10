# Environment variables

Honua is configured entirely through environment variables (or the equivalent `appsettings.json` keys). Nested configuration sections use the double-underscore convention: `Cache__Enabled` binds to `Cache:Enabled`. Defaults below are the compiled/shipped defaults; `.env.example`, `.env.docker.example`, and `.env.production.example` in the repository root are ready-to-copy templates. Runtime configuration metadata is also served at `GET /api/v1/admin/config`.

**Required variables**

| Variable | Required when |
| --- | --- |
| `ConnectionStrings__DefaultConnection` | Always (PostgreSQL provider, the default). |
| `HONUA_ADMIN_PASSWORD` | Production — admin endpoints refuse to operate without it. |

## Database and providers

| Variable | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings__DefaultConnection` | — (**required**) | Primary PostgreSQL/PostGIS connection string. |
| `DataSource__Provider` | `postgres` | Primary provider: `postgres` (read/write), `duckdb`, or `mysql` (read-only). |
| `DuckDB__DatabasePath` | — | Path to the DuckDB database file (when `DataSource__Provider=duckdb`). |
| `DuckDB__ReadOnly` | `true` | Open the DuckDB file read-only. |
| `DuckDB__SpatialExtensionPath` | — | Optional path to a local DuckDB spatial extension binary. |
| `SqlServer__Enabled` | `true` | Register the read-only SQL Server provider alongside the primary backend. |
| `SqlServer__ConnectionString` | — | Default SQL Server connection string (leave unset when using per-layer secure connections). |
| `SqlServer__CommandTimeoutSeconds` | `60` | SQL Server read command timeout. |
| `Oracle__Enabled` | `true` | Register the read-only Oracle provider; set `false` for Native AOT builds. |
| `Oracle__ConnectionString` | — | Default Oracle connection string (leave unset when using per-layer secure connections). |
| `Oracle__CommandTimeoutSeconds` | `60` | Oracle read command timeout. |
| `MySql__ConnectionString` | — | MySQL/MariaDB connection string (when `DataSource__Provider=mysql`). |
| `MySql__EngineFlavor` | `Mysql` | `Mysql` or `MariaDb` — controls WKB axis-order handling. |
| `ArcGisRest__Enabled` | `true` | Register the federated ArcGIS REST read-through provider. |

Provider capabilities and versions: [data sources](data-sources/README.md).

## Security and authentication

| Variable | Default | Purpose |
| --- | --- | --- |
| `HONUA_ADMIN_PASSWORD` | — (**required in production**) | Password for the admin API (`/api/v1/admin/*`). |
| `Authentication__ClientCertificates__Mode` | `Disabled` | Client-certificate auth mode: `Disabled`, `Optional`, `RequiredForNative`, `RequiredForAdmin`, `RequiredForEnvironment`. |
| `Authentication__ClientCertificates__EnvironmentId` | — | Environment id matched by `RequiredForEnvironment` and trust profiles. |
| `Authentication__ClientCertificates__ProtectedAdminPathPrefixes__N` | — | Admin path prefixes protected by client-certificate auth. |
| `Authentication__ClientCertificates__ProtectedGrpcServices__N` | — | gRPC service names protected by client-certificate auth. |
| `Authentication__ClientCertificates__TrustProfiles__N__*` | — | Trust-profile definitions (issuer subjects, SAN types, principal mappings, roles). |
| `Authentication__ClientCertificates__ForwardedCertificate__Enabled` | `false` | Accept proxy-forwarded client certificates. |
| `Authentication__ClientCertificates__ForwardedCertificate__TrustedProxyNetworks__N` | — | CIDR ranges allowed to forward certificates. |
| `Authentication__PortalToken__OAuth2__AllowedRedirectUris__N` | — (empty rejects all) | Redirect-URI allow-list for the ArcGIS Portal OAuth2 bridge. |
| `Authentication__PortalToken__OAuth2__RequirePkce` | `true` | Require PKCE on every authorization-code flow. |
| `Authentication__PortalToken__OAuth2__RotateRefreshTokens` | `true` | Rotate refresh tokens on each use. |
| `SecurityHeaders__EnableHsts` | — | Emit HSTS headers (recommended `true` in production). |
| `SecurityHeaders__HstsMaxAge` | — | HSTS max-age in seconds (e.g. `31536000`). |
| `Honua__SceneAccessSigning__SigningKey` | — | HMAC-SHA256 key for protected 3D Tiles scene access envelopes; required when any scene has an `AccessPolicy`. |
| `Honua__SceneAccessSigning__TokenTtlMinutes` | `15` | Scene access token lifetime. |
| `Honua__SceneAccessSigning__RefreshAfterFractionOfTtl` | `0.5` | Fraction of the TTL after which clients should refresh. |
| `HONUA_DEV_AUTH` | `false` | Development auth bypass; only active when `ASPNETCORE_ENVIRONMENT=Test` and the acknowledgement below is set. Production refuses to start when set. |
| `HONUA_DEV_AUTH_ACK` | — | Must be `i-understand-this-bypasses-auth` for the bypass to activate. |
| `HONUA_ENABLE_OBSERVABILITY_TEST_SEED` | `false` | Dev/Test-only admin fixture seed endpoint; fails startup in production. |
| `Compliance__Soc2ReadinessClaimed` / `Compliance__FedRampReadinessClaimed` | unset | Operator readiness claims for the compliance posture report. |
| `Compliance__DataResidency__Enforced` | `false` | Flips the data-residency policy view and dry-run verdict (does not block egress by itself). |
| `Compliance__Encryption__FipsModeAttested` | `false` | Operator attestation of FIPS 140-2 host mode. |
| `Compliance__DependencyOverrides__*` | unset | Attestations for sidecar-provided capabilities (audit log, SSO, RBAC, transport encryption, data residency). |

OIDC provider configuration (`Oidc__*` — Azure AD, Google, Okta, Auth0, generic) and API-key management are covered in the [authentication guide](../../guides/secure/authentication.md).

## Licensing

| Variable | Default | Purpose |
| --- | --- | --- |
| `Licensing__LicensePath` | — (Community mode) | Path to a signed JSON license envelope; unset runs Community mode. |
| `Licensing__TrustedKeys__{keyId}` | — | Trusted raw Ed25519 public key (base64url) per license key id. |
| `Licensing__AllowAdminUpload` | `false` | Allow license upload through the admin API. |
| `Licensing__ExpiryWarningDays` | `30` | Days before expiry at which warnings are emitted. |

## Caching (Redis)

| Variable | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings__Redis` | — | Redis connection string; unset uses the in-memory fallback only. |
| `HONUA_REDIS_URL` | — | Docker Compose convenience alias for `ConnectionStrings__Redis`. |
| `Cache__Enabled` | `true` | Master switch for metadata/response caching. |
| `Cache__DefaultTtlSeconds` | `300` (shipped config) | Default TTL for cached metadata. |
| `Cache__ServiceTtlSeconds` | `300` (shipped config) | TTL for cached service metadata. |
| `Cache__LayerTtlSeconds` | `300` (shipped config) | TTL for cached layer metadata. |
| `Cache__QueryTtlSeconds` | `30` | TTL for cached query responses. |
| `Cache__NegativeTtlSeconds` | `30` (shipped config) | TTL for negative (missing layer/service) entries. |
| `Cache__JitterPercentage` | `0.2` | TTL jitter to avoid cache stampedes. |
| `Cache__EnableFallback` | `true` | Use in-memory fallback when Redis is unavailable. |
| `Cache__FallbackMaxEntries` | `1000` | Max entries in the in-memory fallback cache. |
| `Cache__ResponseCachingEnabled` | `false` | Cache exact protocol responses separately from metadata. |
| `Cache__RetryIntervalSeconds` | `30` | Redis reconnect interval after a failure. |
| `Cache__KeyPrefix` | `honua:` | Key prefix for shared Redis instances. |

## File storage

| Variable | Default | Purpose |
| --- | --- | --- |
| `FileStorage__Provider` | `Local` | File storage backend: `Local` or S3-compatible (`AwsS3`). |
| `FileStorage__LocalStorage__BasePath` | `/tmp/honua-storage` | Root directory for local file storage. |
| `FileStorage__MaxFileSizeBytes` | `1073741824` | Maximum stored file size (1 GiB shipped default). |
| `FileStorage__AwsS3__BucketName` | — | S3 bucket name. |
| `FileStorage__AwsS3__Region` | `us-east-1` | S3 region. |
| `FileStorage__AwsS3__ServiceUrl` | — | Custom S3 endpoint (e.g. MinIO). |
| `FileStorage__AwsS3__ForcePathStyle` | `true` | Path-style addressing for S3-compatible stores. |
| `FileStorage__AwsS3__AccessKeyId` / `FileStorage__AwsS3__SecretAccessKey` | — | S3 credentials (prefer instance roles where possible). |
| `HONUA_STORAGE_PROVIDER`, `HONUA_S3_BUCKET`, `HONUA_S3_REGION`, `HONUA_S3_SERVICE_URL`, `HONUA_S3_FORCE_PATH_STYLE`, `HONUA_S3_ACCESS_KEY_ID`, `HONUA_S3_SECRET_ACCESS_KEY` | — | Docker Compose convenience aliases for the `FileStorage__AwsS3__*` keys. |

## Imports and limits

| Variable | Default | Purpose |
| --- | --- | --- |
| `Limits__Imports__MaxPreviewSize` | `10485760` (10 MiB) | Max file size for import preview. |
| `Limits__Imports__MaxSyncImportSize` | `52428800` (50 MiB) | Max file size for synchronous imports; larger files run as jobs. |
| `Limits__Imports__MaxImportSize` | `524288000` (500 MiB) | Max file size for any import. |
| `Limits__Imports__MaxPreviewFeatures` | `100` | Max features returned by a preview. |
| `Limits__Imports__BatchSize` | `1000` | Insert batch size for import writes. |
| `Migration__AllowedServiceHostSuffixes__N` | — (unset) | Optional remote GIS migration/import source allowlist. Each entry matches the exact host or its subdomains. Unset permits any otherwise-safe public host; an explicitly empty array rejects all hosts. |
| `Limits__MaxUploadSizeBytes` | `104857600` (100 MiB) | General upload ceiling. |
| `Limits__Query__MaxRecordCount` | `10000` | Max features per query response. |
| `Limits__Query__DefaultRecordCount` | `1000` | Default page size when the client does not specify one. |
| `Limits__Query__MaxOffset` | `1000000` | Max pagination offset. |
| `Limits__Query__QueryTimeout` | `00:00:30` | Per-query timeout. |
| `Limits__Query__MaxBboxAreaSqKm` | `100000` | Max bounding-box query area. |
| `Limits__Geometry__MaxVerticesPerGeometry` | `50000` | Max vertices accepted per geometry. |
| `Limits__Geometry__MaxGeometrySize` | `5242880` (5 MiB) | Max geometry payload size. |
| `Limits__Geometry__MaxCoordinatePrecision` | `8` | Max coordinate decimal precision. |
| `Limits__Edits__MaxFeaturesPerEdit` | `500` | Max features per edit operation. |
| `Limits__Edits__MaxEditsPerTransaction` | `2500` | Max edits per transaction. |
| `Limits__Edits__MaxPayloadSize` | `26214400` (25 MiB) | Max edit payload size. |
| `Limits__Attachments__MaxAttachmentSize` | `5242880` (5 MiB) | Max single attachment size. |
| `Limits__Attachments__MaxAttachmentsPerFeature` | `5` | Max attachments per feature. |
| `Limits__Attachments__AllowedMimeTypes` | `image/*,application/pdf` | Allowed attachment MIME types. |
| `Limits__Tiles__MaxFeaturesPerTile` | see `.env.example` | Max features rendered into one vector tile. |
| `Limits__Tiles__TileTimeout` | see `.env.example` | Per-tile generation timeout. |
| `Limits__Tiles__MaxTileZoom` / `Limits__Tiles__MinTileZoom` | `22` / `0` | Served zoom range. |
| `TileOptions__SimplifyZoom` | see `.env.example` | Zoom below which geometries are simplified. |
| `TileOptions__CacheMaxAge` | see `.env.example` | `Cache-Control: max-age` for tile responses (seconds). |
| `TileOptions__TileExtent` / `TileOptions__TileBuffer` | `4096` / `256` | MVT tile extent and buffer. |
| `Limits__Analytics__MaxInputFeatures` | `100000` | Max input features for spatial analytics queries. |
| `Limits__Elevation__MaxSampleCount` | `500` | Max samples per elevation profile request. |

The effective import limits are also served at `GET /api/v1/admin/import/limits`.

## Observability

| Variable | Default | Purpose |
| --- | --- | --- |
| `HONUA_OBSERVABILITY` | `false` | Enable metrics and health endpoints. |
| `HONUA_OPENTELEMETRY` | `false` | Enable OpenTelemetry distributed tracing. |
| `Observability__Prometheus__Path` | `/metrics` | Native Prometheus scrape endpoint path. |

## CORS

| Variable | Default | Purpose |
| --- | --- | --- |
| `Cors__AllowedOrigins__N` | — (none) | Allowed origin, one index per origin. |
| `Cors__AllowCredentials` | `false` | Allow credentialed cross-origin requests. |

## Feature flags

| Variable | Default | Purpose |
| --- | --- | --- |
| `HONUA_SERVE_API_DOCS` (`ServeApiDocs`) | `true` in Development, else `false` | Serve the interactive API explorer at `/docs` ([details](../openapi-and-explorer.md)). |
| `HONUA_SERVE_STAC_DEMO` (`ServeStacOpsDemo`) | `true` in Development/Test, else `false` | Serve the hosted STAC operations demo at `/samples/stac-ops/`. |
| `HONUA_SKIP_MIGRATIONS` | `false` | Skip database migrations on startup (out-of-band migration flows own their own upgrade safety — the migration-safety settings below do not apply). |
| `Database__MigrationSafety__ContractApplyPolicy` | `Auto` | `Gate` requires explicit approval before pending reviewed contract-phase (schema-narrowing) migrations apply on an existing database. Journal-scoped: fresh installs always provision fully. See [Deploy with Docker Compose — Upgrade & Rollback](../../guides/deploy/docker-compose.md#upgrade--rollback). |
| `HONUA_APPROVE_CONTRACT_MIGRATIONS` | `false` | One-shot operator approval that lets gated contract-phase migrations apply under `ContractApplyPolicy=Gate`. Unset it after the upgrade. |
| `Database__MigrationSafety__BackupCommand` | — (none) | Optional shell command run immediately before contract-phase migrations apply on an existing database (e.g. `pg_dump ...`); non-zero exit aborts the migration run. Configuration-source only — never settable via the admin API or database. |
| `FeatureChangeEvents__Webhook__Enabled` | `false` | Enable outbound webhook delivery of feature-change events. |
| `FeatureChangeEvents__Webhook__Url` / `FeatureChangeEvents__Webhook__Secret` | — | Webhook target URL and HMAC signing secret. |
| `FeatureChangeEvents__Webhook__MaxAttempts` | `5` | Delivery attempts per event (exponential backoff). |

Application-level rate limiting is deferred; enforce rate limits at your edge proxy, load balancer, or WAF.

## Admission and pooling

Database connection pool and query admission:

| Variable | Default | Purpose |
| --- | --- | --- |
| `Limits__Connections__MaxConcurrentQueries` | `200` | Ceiling on concurrently executing database queries. |
| `Limits__Connections__MaxConnectionPoolSize` | `200` | Npgsql pool maximum. |
| `Limits__Connections__MinConnectionPoolSize` | `20` | Npgsql pool minimum. |
| `Limits__Connections__ConnectionIdleLifetimeSeconds` | `600` | Idle connection lifetime. |
| `Limits__Connections__CommandTimeoutSeconds` | `30` | Database command timeout. |
| `Limits__Connections__RequestTimeout` | `00:01:00` | End-to-end request timeout. |
| `Limits__Connections__ConnectionAcquisitionTimeoutSeconds` | `5` | Max wait to acquire a pooled connection. |
| `Limits__Connections__StatementTimeout` / `LockTimeout` | `00:00:30` | PostgreSQL statement and lock timeouts. |
| `Limits__Connections__IdleInTransactionTimeout` | `00:01:00` | PostgreSQL idle-in-transaction timeout. |
| `Limits__Connections__AdaptiveConcurrencyEnabled` | `false` | Adaptive query admission under the concurrency ceiling. |
| `Limits__Connections__AdaptiveConcurrencyMinQueries` | `1` | Adaptive lower bound. |
| `Limits__Connections__AdaptiveConcurrencyInitialQueries` | `0` (= max) | Adaptive starting limit. |
| `Limits__Connections__AdaptiveConcurrencyMaxQueries` | `0` (= `MaxConcurrentQueries`) | Adaptive upper bound. |
| `Limits__Connections__AdaptiveConcurrencyTargetDurationMs` | `100` | Target database lease duration. |
| `Limits__Connections__AdaptiveConcurrencyUpdateIntervalMs` | `1000` | Min interval between adaptive adjustments. |
| `Limits__Connections__Multiplexing` | `false` | Npgsql multiplexing (`false`, `true`, or `auto`). Incompatible with AWS RDS Proxy and transaction-mode poolers — see [PostGIS connection poolers and proxies](data-sources/postgis.md#connection-poolers-and-proxies-rds-proxy-pgbouncer). |

Session settings (`StatementTimeout`, `LockTimeout`, `IdleInTransactionTimeout`, default `search_path`) are applied with `SET` statements after each physical connection opens, so Honua works behind AWS RDS Proxy and PgBouncer (session mode); see the [PostGIS pooler notes](data-sources/postgis.md#connection-poolers-and-proxies-rds-proxy-pgbouncer).

Geoprocessing job admission and executor guardrails:

| Variable | Default | Purpose |
| --- | --- | --- |
| `ExecutionAdmission__Enabled` | `true` | Master switch for geoprocessing admission control (0 disables a dimension). |
| `ExecutionAdmission__MaxConcurrentJobsPerPartition` | `10` | Max active jobs per partition and kind. |
| `ExecutionAdmission__MaxConcurrentJobsGlobal` | `50` | Max active jobs across all partitions. |
| `ExecutionAdmission__MaxSubmissionsPerWindow` | `20` | Max submissions per principal per rate window. |
| `ExecutionAdmission__RateWindowSeconds` | `60` | Sliding rate-window length. |
| `ExecutionAdmission__MaxCostWeightPerPartition` | `20.0` | Max aggregate cost weight active in a partition. |
| `ExecutionAdmission__DefaultRetryAfterSeconds` | `10` | `Retry-After` hint for throttled submissions. |
| `Geoprocessing__Executors__MaxArtifactBytes` | `52428800` (50 MiB) | Max single artifact payload a built-in executor publishes. |
| `Geoprocessing__Executors__OutputRootDirectory` | OS temp dir | Root for file-sink outputs; traversal outside is rejected. |
| `Geoprocessing__Executors__ResultRetention` | `7.00:00:00` | Retention TTL for durable result packages. |

## Related pages

- [Data sources](data-sources/README.md) — provider capability matrix and per-provider configuration.
- [PostGIS configuration](data-sources/postgis.md) — connection strings, extensions, managed-Postgres notes.
- [OpenAPI and the API explorer](../openapi-and-explorer.md) — `/docs` and the runtime spec endpoints.
