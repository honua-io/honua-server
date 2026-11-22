# ADR-0090: One certified reference architecture

## Status

Proposed (2026-11-14), recording founder direction of 2026-11-13. This is the target
architecture, not a claim that the implementation or live qualification is complete.
The implementation evidence below was checked at trunk `9363c27013f99b33e36ef41bd281cff1997fec63`.

## Context

Honua needs one deployment that customers can understand and the product can operate
itself. Optional infrastructure and independent state mechanisms multiply failure
modes, qualification work and the choices an operator must understand. The reference
architecture therefore fixes the substrate and the execution boundaries while preserving
the existing protocol and product capabilities.

The architectural roles are provider-neutral. AWS and ECS Fargate below identify the
chosen implementation and sole certification target, not a requirement to introduce
provider-specific behavior into domain contracts.

This proposal brings together [ADR-0082](0082-postgres-control-plane-state.md),
[ADR-0083](0083-cdn-is-the-shared-cache.md),
[ADR-0085](0085-postgres-realtime-event-log.md),
[ADR-0086](0086-one-generation-for-configuration-and-caches.md), and
[ADR-0087](0087-native-client-protocols.md). Deterministic self-operation remains governed
by [ADR-0062](0062-graduated-ops-autonomy-policy.md), and the prohibition on AI source-data
editing in [ADR-0028](0028-ai-data-editing-not-allowed.md) remains in force.

## Decision

### 1. One server image, one PostgreSQL database, object storage and a CDN

**Rule and reason.** Replicas run the same server image. PostgreSQL holds durable shared
truth; object storage holds artifacts; the CDN serves shared reads. No shared filesystem,
cache server, broker or search service is required. This limits independent failure and
recovery paths. Task-local scratch files and disposable in-process caches are allowed;
they cannot be the only copy of shared state.

**Today.** The [Dockerfile](../../../../Dockerfile) produces the server runtime image.
[JobStoreDatabase](../../../../src/Honua.Jobs/Features/ControlPlane/JobStoreDatabase.cs)
provides the PostgreSQL job substrate;
[AwsS3FileStorage](../../../../src/Honua.Cloud/Aws/Features/FileStorage/AwsS3FileStorage.cs)
and [CloudFrontCdnInvalidator](../../../../src/Honua.Cloud/Aws/Features/Cdn/CloudFrontCdnInvalidator.cs)
provide object storage and CDN integration. ADR-0082 and ADR-0085 describe the shared-state
and realtime replacements.

**Gap.** PostgreSQL is not yet the backing store for all shared truth. With a PostgreSQL
data source, feature locks, OAuth scopes and analysis reports are durable
([PostgresSharedStateRegistration](../../../../src/Honua.Server/Startup/PostgresSharedStateRegistration.cs),
[ReportingServiceCollectionExtensions](../../../../src/Honua.Server/Features/Reporting/ReportingServiceCollectionExtensions.cs)).
[TenantCatalog](../../../../src/Honua.Server/Features/Infrastructure/MultiTenancy/InMemoryTenantCatalog.cs)
and [TenantUsageMeter](../../../../src/Honua.Server/Features/Infrastructure/MultiTenancy/InMemoryTenantUsageMeter.cs)
are still in memory. They need durable implementations, production registration, and
two-replica and restart tests. A full deployment dependency audit must also prove that the
certified shape requires none of the excluded services or additional workload images.

### 2. Published reads use cloud-native files behind the CDN

**Rule and reason.** Published read-heavy data uses PMTiles, GeoParquet and Cloud Optimized
GeoTIFF in object storage, with range reads through the CDN. Private artifacts retain
authorization through the signed delivery path. This removes per-read server work and
lets publication produce immutable, cacheable artifacts.

**Today.** [PMTilesWriter](../../../../src/Honua.Core/Core/Features/Tiles/PMTiles/PMTilesWriter.cs)
and [GeoParquetFeatureWriter](../../../../src/Honua.Hosting/Features/Services/GeoParquetFeatureWriter.cs)
produce cloud-native formats.
[SharedCacheResponse](../../../../src/Honua.Hosting/Features/SharedCaching/SharedCacheResponse.cs)
centralizes shared response cache policy. ADR-0083 and ADR-0087 establish this delivery path.

**Gap.** A format writer does not prove direct CDN delivery. For example,
[CogArtifactEndpoints](../../../../src/Honua.Server/Features/Protocols/Rasters/CogArtifacts/CogArtifactEndpoints.cs)
still proxies range responses through the server. Publication must demonstrate artifact
refresh, access revocation, signed private delivery and direct CDN range reads for each
supported published format.

### 3. gRPC/protobuf is native; OGC and GeoServices REST are adapters

**Rule and reason.** New native clients use the protobuf contracts over the transports
defined by ADR-0087. OGC and GeoServices REST remain complete compatibility adapters to
shared behavior. A typed native contract avoids rebuilding domain rules for each wire format.

**Today.** [GrpcServiceCollectionExtensions](../../../../src/Honua.Server/Features/Protocols/Grpc/GrpcServiceCollectionExtensions.cs)
registers gRPC, and the [protocol features](../../../../src/Honua.Server/Features/Protocols)
contain the gRPC, OGC and GeoServices surfaces.

**Gap.** Registration does not establish workflow parity. Native clients, browser transport,
SDK examples, authentication, edits, sync and job workflows need end-to-end qualification.
Compatibility tests must continue proving existing adapter behavior as native coverage grows.

### 4. Configuration, metadata and data edits have revisions under one generation

**Rule and reason.** Committed configuration, metadata and human-controlled data edits
advance one monotonically increasing generation, atomically with their revisions. Caches,
ETags, publication artifacts and replay evidence identify the generation they represent.
One ordering boundary prevents independently refreshed views from silently disagreeing.

**Today.** [PostgresConfigurationDocuments](../../../../src/Honua.Server/Features/Infrastructure/Configuration/PostgresConfigurationDocuments.cs)
provides versioned configuration documents, expected-revision/generation checks and
transactional batch writes. ADR-0086 supplies the configuration/cache model.

**Gap and relationship to ADR-0086.** ADR-0086 explicitly excludes data edits from the
generation. This proposal extends that target; it does not claim the extension is accepted
or implemented. Adoption needs an explicit reconciliation of ADR-0086, transactional edit
integration, and concurrency tests proving revisions and generation commit or roll back
together. Generation propagation to every delivery and replay path must be verified.

### 5. Managed infrastructure; customers choose Size, Availability and Protection

**Rule and reason.** Managed infrastructure handles the underlying compute, database and
storage operations. Customers choose product-level Size, Availability and Protection
settings, which map deterministically to infrastructure as code. They should not need to
assemble or tune the infrastructure components to obtain the certified deployment.
AWS is the selected infrastructure operator for that deployment.

**Today.** [AwsS3FileStorage](../../../../src/Honua.Cloud/Aws/Features/FileStorage/AwsS3FileStorage.cs)
and [CloudFrontCdnServiceCollectionExtensions](../../../../src/Honua.Cloud/Aws/Features/Cdn/CloudFrontCdnServiceCollectionExtensions.cs)
wire managed storage and CDN services; the latter uses the task-role credential chain.
These integrations do not establish a customer-level infrastructure sizing contract.

**Gap.** The three-choice customer contract is not established by these integrations.
Define and test its mapping, upgrades, backup/restore and availability behavior
through IaC. Application correctness, migrations and recovery verification remain Honua's
responsibility; managed infrastructure alone does not prove self-operation.

### 6. Self-operation is deterministic; agents diagnose and propose

**Rule and reason.** The server evaluates explicit policies and registered deterministic
actions. Agents can inspect evidence, diagnose and propose. They do not insert model
decisions into the server execution loop. This makes operational decisions reproducible
and bounded by the same guardrails as human requests.

**Today.** [OpsAutonomyEvaluator](../../../../src/Honua.Server/Features/Infrastructure/Monitoring/OpsAutonomyEvaluator.cs)
and [PostgresOpsAutonomyPolicyStore](../../../../src/Honua.Db/Postgres/Features/Observability/PostgresOpsAutonomyPolicyStore.cs)
implement the ADR-0062 policy path, with durable policy, a kill switch and bounded eligibility.
[OperationGateway](../../../../src/Honua.Server/Features/ControlPlane/OperationGateway.cs)
routes requests into the canonical operation runtime.

**Gap.** Each supported recovery needs recorded live proof of detection, bounded action,
health verification, audit and recovery/rollback. Source inspection and merged tests do
not constitute the live self-operation gate. New actions must retain the default
propose-only boundary unless they meet ADR-0062's deterministic auto-safe policy.

### 7. Agents are first-class users with identity, scope, budget, preview, undo and one human yes

**Rule and reason.** An agent acts as an identifiable principal with explicit authority and
a bounded budget. Its proposal presents the intended change and available undo before a
human approves the bound proposal once. The server rechecks authority and evidence at
execution; the agent cannot supply its own approval. This makes delegated work inspectable
without requiring a separate operator channel for every tool.

**Today.** [AgentApprovalRule](../../../../src/Honua.Core/Core/Features/Operations/Policy/AgentApprovalRule.cs)
requires human approval for the named agent access-policy and publication operations.
[AiExecutionAdmissionCoordinator](../../../../src/Honua.Hosting/Features/Security/Admission/AiExecutionAdmissionCoordinator.cs)
and [PostgresAiExecutionAdmissionStore](../../../../src/Honua.Db/Postgres/Features/Security/Admission/PostgresAiExecutionAdmissionStore.cs)
provide admission and durable budget accounting.
[OperationApprovalReplayVerifier](../../../../src/Honua.Server/Features/Operations/OperationApprovalReplayVerifier.cs)
provides approval replay verification.

**Gap.** The named approval operations are not proof that every agent workflow has scoped
identity, budget, preview, bound approval and undo. Qualify these together, including expiry,
revocation, changed evidence, retries and duplicate approval. Actions with no safe undo must
say so in their preview and remain subject to their operation policy. This rule does not
authorize AI source-data edits: ADR-0028 remains unchanged. Nor does it remove the separate
deterministic auto-safe policy of ADR-0062.

### 8. One certified deployment shape

**Rule and reason.** One managed container deployment shape keeps the operating contract
and evidence reproducible. Certification and live release qualification target ECS Fargate
with the substrate above.
Other deployment options may remain available without implying equivalent certification.

**Today.** [ADR-0082](0082-postgres-control-plane-state.md) already names ECS Fargate
with multiple tasks as the target. The [Dockerfile](../../../../Dockerfile) supplies a
container image; it does not demonstrate a running deployment of that target.

**Gap.** An architecture declaration and image are not certification. Record a live deployment qualification
covering multiple replicas, restart and failover, backup/restore, CDN publication, generation
consistency, self-operation and the agent workflow. The release requires recorded live
passes of both the self-operation and AI workflow gates; dates and merged PRs are not passes.

## Consequences

Implementation proceeds in independently reviewable slices without removing existing
capabilities. Durable-store conversions need database integration tests that prove
cross-instance visibility, restart persistence, isolation and atomic concurrency behavior.
The deployment and workflow gaps above remain explicit until verified in the certified
shape. This document proposes architecture; it does not approve infrastructure changes,
new spending or a change to the AI data-editing boundary.
