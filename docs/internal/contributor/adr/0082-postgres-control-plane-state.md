# ADR-0082: PostgreSQL holds all shared state; Redis is removed

## Status
Proposed (2026-11-09). Builds on [ADR-0081](0081-postgres-job-substrate.md) and reuses its rules,
its `leases` table, `JobStoreDatabase` and test harness. Supersedes
[ADR-0017](0017-redis-caching-with-fallback.md) and [ADR-0021](0021-redis-usage-and-hybridcache-deferral.md)
in full, and the Redis statements in [ADR-0025](0025-multi-provider-operation-architecture.md)
("Redis and Durability", context item 4, the Redis consequences),
[ADR-0031](0031-durable-job-orchestration-substrate.md) (what ADR-0081 left) and
[ADR-0032](0032-workflow-orchestration-layer.md) ("Persistence"). Decision record: honua-server#6809
(founder ruling 2026-11-09: "lets go all in on postgis jobs. remove redis.").

## Context
On the branch base, 271 source files mention Redis. Besides jobs (ADR-0081) it is the record of
truth for approvals, operation instances and secrets, workflow operations, the data-protection key
ring, pipeline definitions and runs, API keys and OAuth clients, and it carries sessions, tokens,
replay protection, idempotency, leases, catalog invalidation, progress, the change-event log, caches,
rate limits and four realtime backplanes. With no Redis the process substitutes per-node memory, so a
second node silently disagrees with the first. The certified deployment is one ECS Fargate service
with two or more tasks, RDS PostgreSQL, S3 and CloudFront.

## Decision
Every piece of shared state is a row in the system PostgreSQL database or is explicitly per node.
There is no Redis connection, client library, setting, health check, capability key or code path
that depends on one. Each replacement deletes the Redis class and every in-memory fallback that
existed only because Redis was optional. Nothing protects earlier installs: no migration tool, no
dual-store period, no compatibility shim.

House rules (ADR-0081): one statement per transaction on a pooled connection; `FOR UPDATE SKIP
LOCKED`; lease rows; no session advisory locks, `SET` or `LISTEN/NOTIFY`; expiry, leases, consume and
replay decided in SQL against `now()`; `expires_at` plus a bounded sweep; one write path per record;
raw Npgsql; contract tests on PostgreSQL through Testcontainers.

New stores live in `Honua.Server` beside their feature (it references every project and already
holds `PostgresAdminApiKeyStore`); `Honua.Hosting` has no Npgsql reference and gets none. They take
`JobStoreDatabase` and use `ILeaseStore` (`Honua.Core`), which is the three lease statements of
ADR-0081 section 3.5 over `leases`.

Record tables share one shape unless a row below says otherwise: text primary key (`COLLATE "C"`),
`version BIGINT`, the filter columns the queries need, `record JSON` written in the same statement as
those columns, `expires_at`. Create is `INSERT ... ON CONFLICT DO NOTHING`; update is
`UPDATE ... WHERE id = @id AND version = @expected`.

### Inventory
"Truth" is a record of truth; "rebuild" can be lost without losing a decision. Tests are in
`tests/dotnet/Honua.Server.Tests` unless a project is named.

#### A. Approval and operation control plane (14 rows)
| # | Redis use today | Kind | Replacement | Invariant and test |
|---|---|---|---|---|
| A1 | `RedisOperationProposalStore`: proposal JSON, 30-day TTL, active sets, Lua version check | Truth | `operation_proposals`; `ListActiveAsync` is an indexed query on `active`. `UnavailableOperationProposalStore` deleted | Two writers of one version: one wins. An approved proposal survives a restart and never expires while active. `PostgresOperationProposalStoreTests.TrySetAsync_TwoWritersOfOneVersion_ExactlyOneWins`, `ProposalRestartJourneyTests.Proposal_CreatedApprovedAndExecutedAcrossRestart_Succeeds` |
| A2 | `RedisOperationInstanceStore`: handle JSON, 30 days, active set | Truth | `operation_instances`. `UnavailableOperationInstanceStore`, `NonDurableDirectOperationInstanceStore` and the Redis-off direct-operation list deleted | Create-once; version compare-and-set. `PostgresOperationInstanceStoreTests.TryCreateAsync_ConcurrentCreates_ExactlyOneWins`, `...TrySetAsync_StaleVersion_IsRefused` |
| A3 | `RedisOperationSecretStore`: protected value in a hash, 15 minutes, Lua consume | Truth | `operation_secrets`; consume is `DELETE ... WHERE reference_id = @id AND <four bindings match> AND expires_at > now() RETURNING protected_value`. Unavailable and volatile stores deleted | A secret is returned at most once; a wrong binding returns nothing and leaves it; an expired one is never returned. `PostgresOperationSecretStoreTests.Consume_SixteenConcurrentCallers_ExactlyOneReceivesTheValue`, `...Consume_WrongBinding_ReturnsNullAndKeepsTheSecret`, `...Consume_AfterExpiry_ReturnsNull` |
| A4 | `RedisWorkflowOperationStore`: record, package index, active sets, capped sorted sets per target | Truth | `workflow_operations` with `kind`, `status`, `created_at`, `deploy_target_id`, `metadata_package_id`. Paging, newest succeeded deploy and "later deploy exists" are SQL over those columns; the 1,000 and 5,000 caps and `IsTruncated` go away | Create-once; compare-and-set; `HasLaterDeployOfTargetAsync` is exact because the sweep never deletes a deploy while an older deploy of its target is retained. `PostgresWorkflowOperationStoreTests` (the converted Redis suite) plus `...HasLaterDeployOfTargetAsync_AfterRetentionSweep_StillSeesTheLaterDeploy` |
| A5 | `RedisDataProtectionKeyRepository`: Redis list | Truth | `data_protection_keys` (append-only, never swept), `PostgresDataProtectionKeyRepository`, configured through `IConfigureOptions<KeyManagementOptions>`. Keys stay encrypted with the key-ring certificate and startup still refuses a plaintext key | A value protected on one node unprotects on another; an unencrypted key element refuses startup. `PostgresDataProtectionKeyRepositoryTests.Protect_OnOneProvider_UnprotectsOnASecondProviderOverTheSameTable`, `...EnsureAllElementsAreProtected_PlaintextKey_Throws` |
| A6 | `RedisPackageDraftStore`: draft JSON 24 h, index sorted set | Truth, short-lived | `package_drafts` behind `IPackageDraftBacking`; `InMemoryPackageDraftStore` leaves production | A draft saved on one node is read on another by its owner only. `PostgresPackageDraftBackingTests.TryGetAsync_AfterTtl_ReturnsNull`, `...ListLiveOldestFirstAsync_ReturnsOnlyLiveDraftsOldestFirst` |
| A7 | `RedisMcpSessionStore` sessions: JSON, sliding idle TTL | Truth, short-lived | `mcp_sessions`; a touch writes only when the remaining life is under the idle timeout less one minute | A session created on one node is accepted on another until idle expiry. `PostgresMcpSessionStoreTests.TouchAsync_WithinTheLastMinute_DoesNotWrite`, `...GetAsync_AfterIdleTimeout_ReturnsNull` |
| A8 | MCP requests: hash and sorted sets, Lua state machine, 200 ms poll per request | Truth, short-lived | `mcp_requests`; register is one statement that locks the session row and enforces 32 per session and 128 per principal. Each node polls once a second, in one query, for cancellation of its own active requests | Register is set-if-absent; a cancel on one node is seen on the other within 1 s plus the query. `PostgresMcpRequestStoreTests.Register_ThirtyThreeConcurrentRequests_AdmitsThirtyTwo`, `...Cancel_OnAnotherStore_IsObservedByTheOwnerWithinOnePoll` |
| A9 | MCP session removal: pub/sub `mcp:session:removed` | Push | None. A node ends local streams for a removed session on the same one-second poll | `...Remove_OnAnotherStore_EndsTheLocalSessionWithinOnePoll` |
| A10 | WFS stored queries: set plus strings, no atomicity | Truth | `wfs_stored_queries`, key `(scope, lower(query_id))`, 1,000 per scope. No process-static copy | Two creates of one id: one succeeds. `PostgresWfsStoredQueryStoreTests.Create_SameIdOnTwoNodes_SecondIsRefused` |
| A11 | `RedisAiExecutionAdmissionStore` behind keyed `auto\|postgres\|redis` | Truth | `PostgresAiExecutionAdmissionStore` registered directly; the Redis store, keyed selection and `Backend` option deleted | Existing `PostgresAiExecutionAdmissionStoreTests` |
| A12 | `RedisWorkflowDefinitionStore`: definitions, schedule and trigger cursors | Truth | `workflow_definitions`; `workflow_schedule_cursors` retains completed/pending UTC ticks; `workflow_trigger_cursors` retains opaque markers with UTF-16 ordinal ordering. Cursors advance forward only in SQL | A cursor never moves backwards. `PostgresWorkflowDefinitionStoreTests.AdvanceScheduleCursorAsync_OlderFireTime_IsIgnored` |
| A13 | Schedule and trigger fire claims: `SET NX` 24 h | Truth | Rows in `leases` under `workflow-fire:schedule:` and `workflow-fire:trigger:` with hashed composite identities | One node fires a schedule minute. `...TryClaimScheduleFireAsync_EightNodes_ExactlyOneClaims` |
| A14 | `RedisWorkflowRunStore`, `RedisSourceWatermarkStore` | Truth | `workflow_runs`, `source_watermarks` (monotonic merge in SQL), moved minimally for the same reason | Create-once run; a watermark never regresses. `PostgresWorkflowRunStoreTests.TryCreateAsync_Twice_SecondIsRefused`, `PostgresSourceWatermarkStoreTests` (converted) |

Retention (closes #3606): an active proposal, instance, workflow operation or run is never deleted.
Once terminal they are kept for `ControlPlane:Retention:Proposals` (default 365 days),
`:OperationInstances` (30 days), `:WorkflowOperations` (7 days; metadata releases keep their own
30-day setting; a caller's explicit retention wins) and `:WorkflowRuns` (7 days), measured from the
last write. The audit log remains the permanent record of who approved what.

#### B. Security state with expiry (13 rows)
Nothing here is cache-like, so there is **no PostgreSQL `IDistributedCache`**: a generic cache table
would invite caches into the database. Bearer values are stored as their SHA-256, never in clear (today session ids, tokens, codes and
refresh tokens are Redis keys in clear);
payloads that carry identity-provider tokens or PKCE verifiers are protected with the shared key
ring. The per-node memory mirrors are deleted because they honour a credential another node revoked.

| # | Redis use today | Kind | Replacement | Invariant and test |
|---|---|---|---|---|
| B1 | `AdminAuthSessionStore` sessions via `IDistributedCache` plus memory mirror | Truth | `security_tokens` (`kind`, `token_hash`, `payload`, `expires_at`), kind `admin-session` | Sign-in on one node is honoured on the other; sign-out is effective on the next request anywhere. `TwoNodeTests.AdminSession_SignedInOnOneNode_IsHonouredOnTheOther`, `...AdminSession_SignedOutOnOneNode_IsRefusedOnTheOther` |
| B2 | Admin pending sign-in: read then delete | Truth | `security_tokens` kind `admin-pending`, taken with `DELETE ... RETURNING` | A pending sign-in completes once. `PostgresSecurityTokenStoreTests.ConsumeAsync_SixteenConcurrentCallers_ExactlyOneReceivesThePayload` |
| B3 | `PortalOAuthStore` broker sessions and authorization codes: claim key through reflection, plain read-delete without Redis | Truth | `security_tokens` kinds `portal-broker`, `portal-code`, consumed with `DELETE ... RETURNING` | An authorization code is exchanged once across nodes. `PortalOAuthStoreAcrossNodesTests.ConsumeAuthorizationCodeAsync_EightNodesAtOnce_ExactlyOneRedeemsTheCode`, `PortalOAuthRaceConditionTests` (converted) |
| B4 | Refresh tokens (90 days), rotation, restore after a failed issue | Truth | `security_tokens` kind `portal-refresh`; restore re-inserts with the original `expires_at` | A rotated refresh token is spent once; restore never extends its life. `PortalOAuthStoreAcrossNodesTests.RefreshToken_RotatedOnOneNode_CannotBeReplayedOnAnother`, `PortalOAuthRaceConditionTests` (converted) |
| B5 | Principal revocation markers (91 days) | Truth | `security_tokens` kind `portal-revocation`, key the principal | A refresh token issued before a revocation is refused on every node. `PortalOAuthStoreAcrossNodesTests.RevokePrincipalAsync_OnOneNode_RejectsEarlierRefreshTokensOnAnother`, `PortalOAuthRefreshRevalidationTests` (converted) |
| B6 | `PortalTokenIssuer` tokens | Truth | `security_tokens` kind `portal-token`; revoke is a delete that must report one row or already-absent | A revoked token is refused by the next request on any node. `TwoNodeTests.PortalToken_RevokedOnOneNode_IsRefusedOnTheOther` |
| B7 | `ScopedJobTokenIssuer` tokens | Truth | `security_tokens` kind `job-token` | A job token validates on the node the worker calls back to. `ScopedJobTokenPipelineTests` (converted) |
| B8 | OIDC token replay and MCP continuation binding: `SET NX`, Lua bind, memory path unless fail-closed | Truth | `token_replay` (`replay_key`, `marker`, `expires_at`): register is `INSERT ... ON CONFLICT DO UPDATE ... WHERE expired`; bind is `UPDATE ... WHERE marker IN (registered, this continuation)`. The memory path, the per-token lock table and the dead `AtomicTokenReplayProtection` are deleted. `ReplayProtectionFailClosed` keeps its meaning for a store outage: refuse, or admit unrecorded | A single-use token is accepted once across nodes; a continuation binds once. `TokenReplayAcrossNodesTests` (the continuation and HTTP API rules on two nodes), `TokenReplayScopeTests` (converted) |
| B9 | `SamlAssertionReplayCache` | Truth | `token_replay` under a `saml:` key, through a synchronous Npgsql call (the validator is synchronous) | An assertion or response id is accepted once across nodes. `TokenReplayAcrossNodesTests.SamlAssertion_PresentedOnEightNodesAtOnce_IsAcceptedOnce` |
| B10 | applyEdits and replica-upload idempotency: `SET NX`, Lua complete and release, process dictionary without Redis, fail open | Truth | `idempotency_reservations` (`scope_hash`, `reservation_token`, `receipt`, `expires_at`): reserve, complete-if-owner, release-if-owner. A store failure on reserve lets the request run unrecorded, as before | One of eight concurrent requests with one key runs; a recorded response replays on another node. `ApplyEditsIdempotencyAcrossNodesTests`, the idempotency store tests (converted) |
| B11 | `GrpcApplyEditsIdempotencyStore` reservation, wait and unpublished-receipt queue | Rebuild | Deleted; what remains is `GrpcApplyEditsReceipts` (scope, local gate, local results, durable receipt replay). The receipt row written in the edit transaction (migration 130) decides the winner. A keyed edit that rolls back on a serialization failure, deadlock or unique conflict re-reads the receipt once and replays it if one committed | A duplicate commit is rolled back by the receipt key and the first receipt is replayed. `GrpcApplyEditsIdempotencyTests` (converted off the Redis fixture), `GrpcApplyEditsCrossNodeIdempotencyTests.ApplyEdits_SameKeyOnTwoNodes_CommitsOnce_AndTheOtherNodeReplaysTheFirstReceipt`, `...ApplyEdits_SameKeyRacingOnTwoNodes_TheLaterCommitIsRolledBack_AndReplaysTheFirstReceipt`, `...ApplyEdits_SameKeyUpdatingTheSameRowsOnTwoNodes_TheConflictingNodeReplaysTheFirstReceipt` |
| B12 | `RedisAdminApiKeyStore`, `InMemoryAdminApiKeyStore`, index maintenance; `RedisOAuthClientStore`, `InMemoryOAuthClientStore` | Truth | `PostgresAdminApiKeyStore` always; new `oauth_clients` with a unique `client_id`. The sweep deletes approved-operation credentials at expiry, as Redis did. In-memory stores become test doubles in the test projects | A key revoked on one node is refused on the next request on the other. `TwoNodeTests.ApiKey_RevokedOnOneNode_IsRefusedOnTheOther`, `PostgresOAuthClientStoreTests.ValidateSecretAsync_AfterDelete_ReturnsNull` |
| B13 | Cloud temporary-file tokens and quota lease; `DistributedReplicaStore` (7 days); the per-principal submission rate window in `ExecutionAdmissionEvaluator` | Truth | `security_tokens` kind `temp-file`; a `leases` row for the quota; offline replicas are already rows written through `IReplicaRepository`, so the cache in front of them is deleted and `CachingReplicaStore` reads the repository; the rate window is a count over `execution_jobs` by kind and the admission rate principal stamped on each job (a lookup key; for geoprocessing it is tenant-scoped, so it differs from the requester), measured back from the database clock and taken under the submission lease (C3) | A download token minted on one node resolves on the other; the submission rate is exact across nodes. `TemporaryFileServiceTests` (converted), `ExecutionAdmissionMultiNodeTests` (converted) |

#### C. Coordination (8 rows)
| # | Redis use today | Kind | Replacement | Invariant and test |
|---|---|---|---|---|
| C1 | `RedisLeaseCoordinator` (webhook dispatch, temp-file quota, export, tile and retry claims) | Coordination | `LeaseHolder` over `ILeaseStore`: acquire, renew at a third of the duration, signal loss | At most one holder at any database time; a holder that cannot renew stops. `PostgresLeaseStoreTests.TryAcquireAsync_SixteenContenders_ExactlyOneHolds`, `...TryAcquireAsync_AfterExpiry_AnotherOwnerTakesOver`, `LeaseHolderTests.LeaseLostToken_WhenRenewalIsRefused_IsCancelled` |
| C2 | `IDistributedLeaderElection`: Redis lock in imports; always-leader for CRS warm-up; the unused `RedisDistributedLeaderElection`; alerts use a session advisory lock on a dedicated connection | Coordination | One `LeaseLeaderElection` (30 s lease, 10 s renewal) for import, migration batches and alert evaluation. CRS warm-up is per node and takes no election. `PostgresAdvisoryLockLeaderElectionStrategy` and `SingleInstanceLeaderElectionStrategy` deleted | One leader per key; after a leader stops, another leads within the lease duration. A leader's work must still be idempotent or fenced by its own record. `LeaseLeaderElectionTests.TryAcquireLeadershipAsync_EightNodes_ExactlyOneLeads`, `...Leadership_AfterTheLeaderStopsRenewing_MovesWithinTheLeaseDuration` |
| C3 | `ExecutionAdmissionCoordinator` submission lease | Coordination | `leases` row `execution-admission:submission`, same options | Snapshot, admit and create never interleave across nodes. `ExecutionAdmissionMultiNodeTests`, `TileExportAdmissionSharedLeaseTests` (converted) |
| C4 | `RedisVersionLock` with in-process fallback | Coordination | `PostgresVersionLock` over `ILeaseStore` (15 min, renewed); `InProcessVersionLock` becomes a test double | Two nodes cannot reconcile or post one version together. `PostgresVersionLockTests.TryAcquireAsync_SecondNode_ReturnsNullUntilRelease` |
| C5 | `RedisMetadataV2GraphCacheInvalidator` pub/sub | Push | Revision probe, below | `TwoNodeTests.CatalogChange_OnOneNode_IsServedByTheOtherWithinTheInterval`, `...PermissionRevocation_OnOneNode_IsEnforcedOnTheNextSensitiveOperationOnTheOther` |
| C6 | `DistributedCacheRefreshCoordinator` claims and a channel nothing publishes to | Rebuild | Its local path only | Existing coordinator tests, without the multiplexer |
| C7 | "MultiNode requires Redis" and the `Cache:EnableFallback` warning in `ConfigurationValidationService`; `McpSessionModeResolver` and API-key selection by host type | Rule | Deleted. MultiNode still refuses local file storage | `MultiNodeWithoutRedisStartupTests` |
| C8 | Every place that chooses behaviour by whether a multiplexer is registered (stores, fallbacks, capability causes) | Rule | Deleted with the multiplexer; a registration no longer has an alternative | Final check: `git grep -n -i redis -- src docker-compose.yml` is empty |

**Catalog coherence.** `CachingMetadataV2GraphProvider` already compares the cached snapshot's etag
with `SELECT etag FROM metadata_v2_current WHERE environment = @environment` (a primary-key read of
one row). It does so per request, only on Lambda with no Redis. It becomes the only mechanism, on
every host:
- A request that reads (GET, HEAD, OPTIONS, outside the admin API) uses the snapshot if this node
  (`MetadataV2GraphRevisionProbeSchedule`, one per process) probed within `Cache:MetadataGraphRevalidationSeconds` (default 2, range 0 to 60; 0 probes every
  request). Otherwise it probes first. There is no timer: an idle node sends nothing.
- Every other request (any other method, and everything under the admin API) probes before it reads
  the catalog. An edit, a publish, an admin mutation or a tool call therefore sees the layer
  security and configuration committed before it started.
- The node that commits a change invalidates its own snapshot, as now.

Worst-case staleness for a read on another node is the interval plus one probe. Cost is at most one
primary-key read per interval per serving node, plus one per non-read request. Roles, row and field
policies, API keys and tokens are read from the database on each request and are never stale.
`Cache:MetadataGraphRevalidateOnRead` and the Lambda special case are deleted.

#### D. Older job managers and progress (9 rows)
| # | Redis use today | Kind | Replacement | Invariant and test |
|---|---|---|---|---|
| D1 | `UniversalProgressStore`: progress via `IDistributedCache`, active sets, lock key for compare-and-set | Truth | `operation_progress` (`operation_id`, `operation_type`, `status`, `payload`, `expires_at`); compare-and-set is one `UPDATE` guarded by the status, and replace is one `UPDATE` guarded by the stored payload. Built with group B because every job family reports through it | Status asked of any node is the status last written; a cancel never loses to a late progress write. `PostgresUniversalProgressStoreTests.TrySetProgressAsync_ExpectedStatusChanged_IsRefused`, `...TryReplaceProgressAsync_TwoWriters_ExactlyOneWins` |
| D2 | `RedisImportJobManager`, `GeoServerImportJobManager`: Redis lists, leader lock, request store, memory fallback that refuses work in production | Truth | Execution job kinds `GeoservicesImport` and `GeoServerImport`; the request is the job specification; the lists, leader loop and `ImportJobManagerState` are deleted. Batch advance keeps a leader (C2) | An accepted import runs once and survives the node that accepted it. `GeoservicesImportJobTests.Import_AcceptedOnOneNode_RunsOnAnotherAfterTheFirstStops` |
| D3 | `ExportJobService`: in-process channel, Redis claim | Truth | Execution job kind `Export` | `ExportJobTests.Export_ClaimedByTwoWorkers_RunsOnce` |
| D4 | `TileOperationJobService`: in-process channel, Redis claim | Truth | The existing `TileCache` job kind is the only path | `TileOperationJobTests.Start_AlwaysCreatesAnExecutionJob` |
| D5 | `RedisVersionJobStore` | Truth | `version_jobs`; the status may only move forward, in the `UPDATE` predicate | `PostgresVersionJobStoreTests.SaveAsync_BackwardTransition_IsIgnored` |
| D6 | `RedisTileCacheGenerationCheckpointStore` | Rebuild (resume point) | `tile_generation_checkpoints` | `...LoadAsync_OnAnotherStore_ReturnsTheSavedCheckpoint` |
| D7 | `InMemoryFeatureChangeEventStore`: Lua cursor, index, 7 days, 20,000 events | Truth | `feature_change_events`; the cursor comes from a one-row counter updated in the append statement, so cursor order is commit order and a reader never skips a late commit | Cursors are gapless in commit order; an event id is appended once. `PostgresFeatureChangeEventStoreTests.AppendAsync_ConcurrentWriters_ProducesContiguousCursors`, `...AppendAsync_SameEventIdTwice_ReturnsTheFirstCursor` |
| D8 | `FeatureChangeRetryQueue` | Rebuild | Deleted. Only PostgreSQL accepts feature writes, and it writes the outbox row in the edit transaction for every protocol mutation path; every other provider is read-only and has no outbox table, so the post-commit publish is best effort and a failed append is logged with the event identity. Dispatched outbox rows gain a retention sweep, which needs an index on `dispatched_at` first | `FeatureStreamPublisherTests.PublishAsync_WhenAppendFails_LogsTheDroppedEventAndDoesNotBroadcast` |
| D9 | Webhook delivery cursor, dispatch lease, per-event delivery state | Truth | `stream_cursors`, a `leases` row, `webhook_deliveries` | An event is delivered by one node at a time and marked once. `FeatureChangeWebhookDispatcherTests` (converted) |

#### E. Rebuildable state and realtime (9 rows)
| # | Redis use today | Replacement | What an operator or client sees |
|---|---|---|---|
| E1 | `RedisCacheService`, index maintenance, `DistributedCacheKeyIndex`, dead `RedisCacheManagerAdapter` | The same service over process memory, renamed; `ICacheService`, `IResponseCache` and `ResponseCacheUtilities` keep their shape (honua-server#7247 is changing them) | Each node caches for itself. Exact response caching stays off unless `Cache:ResponseCachingAllowInMemoryFallback` is set; with it on, another node can serve a response up to its lifetime after an edit |
| E2 | Redis output cache | In-memory output cache | Per node; the CDN is the shared cache |
| E3 | `RedisTileCacheKeyIndex` (size and LRU index, mutation lease), eviction sweep, inventory | Deleted, with no replacement inventory. Tiles are still written to object storage; their cache headers, CDN invalidation and lifetime rule belong to honua-server#7247 and the Terraform | No server-side size cap or inventory endpoint for the object-store tile cache |
| E4 | Rate-limit sliding window | The existing per-node fixed window | A limit of N is N per node; with T tasks a client can reach N×T. Documented |
| E5 | Admin SignalR backplane | Deleted; groups are always joinable and deliver the events raised on that node; `backplaneEnabled` leaves the status payload. The server does not keep the old refusal | A console connected to one task misses events raised on another, so it must keep polling while connected. Console today treats a refused join as "no backplane"; it changes in step |
| E6 | `RedisCollaborationSessionBackplane` | Deleted | Presence, cursors and live operations reach only users connected to the same task. Experimental |
| E7 | Feature stream broadcast, backlog, recovery timer | Deleted | A stream delivers live the changes committed through its own task; replay by cursor returns every change (D7). Experimental |
| E8 | Observation stream broadcast | Deleted | Same as E7 for SensorThings observations. Experimental |
| E9 | `LicenseCapacityMeter`, `LicenseCapacityCalculator`, models, options, `GET /license/capacity`, `POST /license/capacity/surge`, the `capacity` field of license status | Deleted (#4720 closed: no usage or capacity metering). A license file that carries capacity terms is still accepted and the terms are ignored; ADR-0080 and `NOTICE` stop naming capacity metering | The two endpoints and the field no longer exist |

#### F. Deletion (7 rows)
| # | What | Detail |
|---|---|---|
| F1 | Connection wiring | `Program.cs` Redis sections, `RedisConnectionSelector`, secret-reference resolution, thaw probe, `RedisClientFault`, all of `Honua.Hosting/Features/Infrastructure/Redis`, the three `Honua.Core` `IRedis*` interfaces, the unused `DataIntegrityCoordinator`, AppHost Redis resource, Redis telemetry instrumentation |
| F2 | Durability attestation | `RedisDurabilityAttestor`, the worker reporter, `DurableJobSubstrateOptions` Redis fields, startup gate text, `limits.job.redisDurability` in the capability manifest |
| F3 | Capability and entitlement | `caching.redis` in `FeatureCatalog` and the generated catalogs; the two guard-test exceptions for `jobs.durable-runtime` and `operations.proposals`; `missingDependency: "redis"` and `missingEntitlement: "caching.redis"` refusals; `GET /operations/cache/redis` and the Redis fields of cache health and statistics |
| F4 | Health | The `redis` health check and its tags |
| F5 | Packages | `StackExchange.Redis` (7 projects), `Microsoft.Extensions.Caching.StackExchangeRedis`, `Microsoft.AspNetCore.OutputCaching.StackExchangeRedis`, `Microsoft.AspNetCore.SignalR.StackExchangeRedis`, `Aspire.StackExchange.Redis.DistributedCaching`, `Aspire.Hosting.Redis`, `OpenTelemetry.Instrumentation.StackExchangeRedis`, `Testcontainers.Redis` |
| F6 | Compose and tests | The Redis service in `docker-compose.yml` and the other compose files; `docker-compose.no-redis.yml`; `Honua.TestKit.Redis`, `RedisFixture`, `RedisCollection`, `AiAdmissionRedisFixture`; Redis-only tests are converted or deleted with their subject |
| F7 | Documents | `docs/concepts/architecture.md` and diagrams, the deploy guides, backup and restore, scaling and performance, troubleshooting, editions, environment variables, the OKF capability pages, `REDIS_FALLBACK_PATTERNS.md`, generated API specifications |

### State kind to table
The reference for pipelines (#7151): runs, definitions, watermarks and schedule claims are already
on the tables below and on `leases` and `execution_jobs`.

| State | Table | Key | Swept |
|---|---|---|---|
| Jobs, queue, logs, result packages | ADR-0081 tables | | ADR-0081 |
| Leases, leadership, fire claims | `leases` | `lease_key` | expired |
| Proposals | `operation_proposals` | `proposal_id` | terminal, 365 d |
| Operation instances | `operation_instances` | `operation_instance_id` | inactive, 30 d |
| Operation secrets | `operation_secrets` | `reference_id` | 15 min |
| Deploy, metadata-release, restore operations | `workflow_operations` | `operation_id` | terminal, 7 d or 30 d |
| Data-protection keys | `data_protection_keys` | identity | never |
| Pipeline definitions | `workflow_definitions` | `workflow_id` | never |
| Pipeline runs | `workflow_runs` | `run_id` | terminal with all children settled, 7 d |
| Pipeline watermarks | `source_watermarks` | `(pipeline_id, source_id)` | never |
| Pipeline schedule cursors | `workflow_schedule_cursors` | `workflow_id` | never |
| Pipeline trigger cursors | `workflow_trigger_cursors` | `(workflow_id, cursor_kind)` | never |
| Realtime and webhook cursors | `stream_cursors` | `cursor_key` | never |
| Package drafts | `package_drafts` | `(kind, draft_id)` | 24 h, 500 per kind |
| MCP sessions and requests | `mcp_sessions`, `mcp_requests` | `session_id`; `(session_id, request_key)` | idle 30 min |
| WFS stored queries | `wfs_stored_queries` | `(scope, lower(query_id))` | never |
| AI admission | migrations 140 and 142 | | existing |
| Sessions, tokens, codes, revocations | `security_tokens` | `(kind, token_hash)` | at expiry |
| Token and assertion replay | `token_replay` | `replay_key` | at expiry |
| Edit idempotency | `idempotency_reservations`; `grpc_apply_edits_receipt` | `scope_key` | 24 h |
| API keys, OAuth clients | `admin_api_keys`, `oauth_clients` | `id` | approved-operation keys at expiry |
| Offline replicas | existing replica tables (`IReplicaRepository`) | `replica_id` | existing |
| Operation progress | `operation_progress` | `operation_id` | 24 h |
| Version jobs | `version_jobs` | `job_id` | 7 d |
| Tile generation checkpoints | `tile_generation_checkpoints` | `generation_id` | 24 h |
| Change events, webhook deliveries | `feature_change_events`, `webhook_deliveries` | `cursor`; `event_id` | 7 d and 20,000 events |

### Background work after this change
There is **one sweep**: the retention sweep of ADR-0081 section 3.12, which also takes the tables
above. Every 10 minutes on every serving node it runs one statement per swept table (18 here, 4 in
ADR-0081): `DELETE` of at most 1,000 rows chosen by an index range on `expires_at` with `FOR UPDATE
SKIP LOCKED`, repeated only while a batch is full. An idle database costs 22 index probes per node
per 10 minutes. It never scans or locks a whole table. The job branch has not pushed its sweep
service; this branch writes one against that description and the two become one service on rebase.

Polls, none of which deletes: the two job reconcilers (ADR-0081); a held lease or leadership renews
with one `UPDATE` every third of its duration; the MCP cancellation poll is one query per second per
node while that node has active requests; the catalog probe is on demand.

### Connections
Every store uses the shared pool (200 maximum, 20 minimum on the serving host) and holds no
connection between statements. Removed: the Redis multiplexers and the alert leader's dedicated
advisory-lock connection. Added per request: one primary-key read to validate a session or token;
one statement the first time a node sees a bearer token (B8); one probe for a non-read request (C5).
The operation-secret store, the key ring and SAML replay are called synchronously by ASP.NET and use
synchronous Npgsql commands; they are rare. Behind RDS Proxy nothing here pins a connection; the
pinning caveat of ADR-0081 section 7 (`Database:SessionSettings:Mode`) is unchanged.
`PostgresAiExecutionAdmissionStore` already uses `SET LOCAL` and a transaction-level advisory lock.
Both end with the transaction, which is not the session pinning the rule is about, so it is unchanged.

### What two nodes guarantee
Guaranteed across nodes: jobs, approvals, operation secrets (once), key ring, sign-in and sign-out,
tokens and their revocation, API keys, OAuth single use, replay rejection, edit idempotency, leases
and leadership, version locks, operation and import status, change-event replay, MCP sessions and
cancellation within a second, catalog changes within the probe interval for reads and immediately
for everything else.

Per node, by design: response, output and metadata caches; rate limits (N per node); admin live
events; collaboration presence and cursors; live feature and observation streams; the stale-refresh
coordinator; CRS warm-up; the license re-read (each node, every minute).

Not provided without a push channel: sub-second cross-node delivery of live events. A client sees
live events only from the task it is connected to and must poll or replay for the rest.

## Order of work
1. Group A stores and tests. 2. Groups B and C stores, and `operation_progress` (D1). 3. One wiring
change that switches A, B, C and D1 and deletes their Redis classes: after it the certified
deployment needs no Redis. 4. The rest of D. 5. E and F. The branch is coherent after 3 and after 4.

The job branch registers `JobStoreDatabase` and the job stores at every host and has deleted the
Redis job stores. Left for step 3 here: the remaining Redis block in `Program.cs`, the Redis-named
causes and fields of `DurableJobSubstrateOptions`, and the control-plane Lambda event path
(`ControlPlaneEventHandler`, `ControlPlaneLambdaEventTargets.RunBackstopAsync`), which resolves the
workflow operation store and must move to the PostgreSQL one in the same change that deletes
`RedisWorkflowOperationStore`. `operations.proposals` becomes available through
`CapabilityManifestOptionsSnapshot.ProposalStoreAvailable` once the PostgreSQL proposal store is
registered; `CapabilityManifestEndpointTests.Manifest_WithNoRedis_ReportsProposalsAvailable` proves
it.

## Alternatives considered
- **A PostgreSQL `IDistributedCache` for sessions and tokens.** No consume-once, no hashing of keys,
  and it keeps the call sites' read-then-delete races.
- **One table per security kind.** Six tables with identical columns and statements; exactly-once
  comes from `DELETE ... RETURNING` on the primary key, which one table gives equally.
- **`LISTEN/NOTIFY` for invalidation and fan-out.** Pins proxy connections and is lost by a frozen
  Lambda (#6760, #6765).
- **Polling the change-event table to fan streams out across nodes.** Possible later on D7; ruled
  out now because realtime is Experimental and per node.
- **A probe timer per node.** Costs queries while idle and does not run in a frozen Lambda.

## Consequences
- A deployment is PostgreSQL, object storage and the server image. Community edition gets approvals
  and multi-node coordination; `caching.redis` no longer exists.
- Authentication adds one indexed read per request, and bearer values are no longer stored in clear.
- The key-ring certificate (`Operations:SecretChannel:KeyRingCertificate*`) is required outside
  Development and Test on every deployment, as it already was on any deployment with Redis.
- Wire changes: the capacity endpoints, the Redis cache endpoint and fields, `redisDurability`,
  `backplaneEnabled`, and the `redis` refusal values are removed.
- Console contract (honua-console at `96bd5db4`): no page or service calls `/license/capacity`,
  `/license/capacity/surge` or `/operations/cache/redis`; they appear only as entries of the generated
  operation table in `wwwroot/_studio/console.js`, which is regenerated from the API specification.
  Console does not read `backplaneEnabled`. Its ops-health and deploy-operation realtime clients treat
  a refused group join as "no backplane" and fall back to refresh; once joins always succeed they
  must keep refreshing while connected, because live events are node-local.
- Release note: "Redis is no longer used. Let running jobs and pending approvals finish before
  upgrading from an earlier pre-release build; sessions and tokens are not carried over."

Workflow definition and run `SetAsync` retain the unconditional replacement contract. Workflow history reads remain unbounded; occurrence fencing and bounded history are separate pipeline follow-up work. Active runs, including cancelled runs with unsettled children, remain discoverable for reconciliation past their retention deadline.
