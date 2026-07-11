// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Models;

// -----------------------------------------------------------------------
// Tool input shapes (arguments passed via tools/call)
// -----------------------------------------------------------------------

/// <summary>
/// Arguments for <c>honua_validate_plan</c> and <c>honua_dry_run_plan</c>.
/// </summary>
internal sealed class McpPlanArgument
{
    [JsonPropertyName("plan")]
    public McpPlanInput? Plan { get; set; }
}

/// <summary>
/// Arguments for <c>honua_execute_plan</c>.
/// </summary>
internal sealed class McpExecutePlanArgument
{
    [JsonPropertyName("plan")]
    public McpPlanInput? Plan { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// Arguments for <c>honua_cancel_job</c>.
/// </summary>
internal sealed class McpCancelJobArgument
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }
}

/// <summary>
/// Arguments for <c>honua_propose_operation</c>: submit an in-scope mutating
/// control-plane operation through the approval gateway (#1696).
/// </summary>
internal sealed class McpProposeOperationArgument
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("executionPayload")]
    public string? ExecutionPayload { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// Arguments for <c>honua_ops_findings</c>: list active deterministic ops
/// findings, or fetch a single finding by its stable identifier.
/// </summary>
internal sealed class McpOpsFindingsArgument
{
    [JsonPropertyName("findingId")]
    public string? FindingId { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("rule")]
    public string? Rule { get; set; }
}

/// <summary>
/// Arguments for <c>honua_alert_events</c>: cursor-paged alert-event reads
/// across GIS alert rules and ops notifications.
/// </summary>
internal sealed class McpAlertEventsArgument
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("rule")]
    public string? Rule { get; set; }

    [JsonPropertyName("lifecycleState")]
    public string? LifecycleState { get; set; }

    [JsonPropertyName("from")]
    public DateTimeOffset? From { get; set; }

    [JsonPropertyName("to")]
    public DateTimeOffset? To { get; set; }

    [JsonPropertyName("pageSize")]
    public int? PageSize { get; set; }

    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }
}

/// <summary>
/// Arguments for <c>honua_operate_events</c>: paged reads over the fused
/// Operate timeline.
/// </summary>
internal sealed class McpOperateEventsArgument
{
    [JsonPropertyName("kind")]
    public IReadOnlyList<string>? Kind { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("operationId")]
    public string? OperationId { get; set; }

    [JsonPropertyName("releaseId")]
    public string? ReleaseId { get; set; }

    [JsonPropertyName("from")]
    public DateTimeOffset? From { get; set; }

    [JsonPropertyName("to")]
    public DateTimeOffset? To { get; set; }

    [JsonPropertyName("pageSize")]
    public int? PageSize { get; set; }
}

/// <summary>
/// Arguments for <c>honua_deploy_operations</c>: list deploy operations or
/// fetch one deploy operation by stable operation id.
/// </summary>
internal sealed class McpDeployOperationsArgument
{
    [JsonPropertyName("operationId")]
    public string? OperationId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("page")]
    public int? Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int? PageSize { get; set; }
}

/// <summary>
/// Arguments for <c>honua_propose_rollback</c>: propose rolling a deploy
/// target forward to a prior revision through the shared operation gateway.
/// </summary>
internal sealed class McpProposeRollbackArgument
{
    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    [JsonPropertyName("toRevision")]
    public string? ToRevision { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// Output for <c>honua_propose_operation</c>. On <c>requiresApproval</c> the
/// agent polls the <c>resourceUri</c> until the proposal resolves (#1696).
/// </summary>
internal sealed class McpProposeOperationOutput
{
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; set; }

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; set; }

    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }

    [JsonPropertyName("executionOperationId")]
    public string? ExecutionOperationId { get; set; }

    /// <summary>
    /// Operation classes that have a genuinely registered executor and are therefore routable
    /// through the gateway (#2563). Reported on every response, including rejections, so an
    /// agent proposing an unsupported kind (Seed today) learns the real supported set instead of
    /// discovering it only after a dead-end <c>NotSupported</c> outcome. Null when the executor
    /// catalog is unavailable.
    /// </summary>
    [JsonPropertyName("supportedKinds")]
    public string[]? SupportedKinds { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Read-only projection of the operation kinds that the live gateway can route.
/// </summary>
internal sealed class McpSupportedOperationKindsOutput
{
    /// <summary>
    /// Stable <see cref="Honua.Core.Features.Guardrails.Domain.OperationClass"/> identifiers
    /// backed by registered operation executors.
    /// </summary>
    [JsonPropertyName("supportedKinds")]
    public IReadOnlyList<string> SupportedKinds { get; set; } = [];
}

/// <summary>
/// Arguments for <c>honua_publish_service</c>: publish a source table as a
/// hosted layer/service through the canonical <c>service.publish</c> operation
/// (#1951). Field names mirror the <c>service.publish</c> operation parameters
/// the <c>ServicePublishExecutor</c> maps onto a <c>LayerPublishRequest</c>.
/// </summary>
internal sealed class McpPublishServiceArgument
{
    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; set; }

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("table")]
    public string? Table { get; set; }

    [JsonPropertyName("layerName")]
    public string? LayerName { get; set; }

    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("geometryColumn")]
    public string? GeometryColumn { get; set; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; set; }

    [JsonPropertyName("srid")]
    public int? Srid { get; set; }

    [JsonPropertyName("primaryKey")]
    public string? PrimaryKey { get; set; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<string>? Fields { get; set; }
}

/// <summary>
/// Output for <c>honua_publish_service</c>. Projects the canonical
/// <c>OperationHandle</c> returned by the operations dispatcher: a
/// <c>Completed</c> publish carries the resulting service URI, layer id, and
/// metadata revision; a <c>RequiresApproval</c> outcome carries the approval
/// lane the agent must wait on; a <c>Queued</c> outcome carries the durable job
/// id (#1951).
/// </summary>
internal sealed class McpPublishServiceOutput
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; set; }

    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("handleId")]
    public string HandleId { get; set; } = string.Empty;

    [JsonPropertyName("serviceUri")]
    public string? ServiceUri { get; set; }

    [JsonPropertyName("layerId")]
    public string? LayerId { get; set; }

    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; set; }

    [JsonPropertyName("metadataRevision")]
    public long? MetadataRevision { get; set; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    [JsonPropertyName("approvalLane")]
    public string? ApprovalLane { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Structured result of invoking a published operations-toolset operation projected
/// as a first-class MCP tool (#2483, ADR-0056 Increment 4). Projects the canonical
/// <c>OperationHandle</c> returned by the operations dispatcher, plus the
/// determinism/cache provenance the published-tool contract adds: <c>deterministic</c>
/// reports whether the backing descriptor is AI-free, and <c>cacheHit</c> /
/// <c>cacheKey</c> report the param-keyed cache decision for a deterministic,
/// read-only invocation.
/// </summary>
internal sealed class McpOperationToolOutput
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; set; }

    /// <summary>
    /// Whether the backing operation descriptor is deterministic (AI-free). A
    /// deterministic tool is what "deterministic mode" (a toolset with AI off)
    /// publishes; only deterministic, read-only invocations are param-keyed cached.
    /// </summary>
    [JsonPropertyName("deterministic")]
    public bool Deterministic { get; set; }

    /// <summary>
    /// True when this result was served from the param-keyed deterministic cache
    /// rather than re-executed. Always false for non-deterministic or side-effecting
    /// operations, which are never cached.
    /// </summary>
    [JsonPropertyName("cacheHit")]
    public bool CacheHit { get; set; }

    /// <summary>
    /// The param-keyed cache key for a deterministic, read-only invocation
    /// (operation id + catalog version + normalized parameters). Null when the
    /// operation is not cacheable.
    /// </summary>
    [JsonPropertyName("cacheKey")]
    public string? CacheKey { get; set; }

    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("handleId")]
    public string HandleId { get; set; } = string.Empty;

    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    [JsonPropertyName("approvalLane")]
    public string? ApprovalLane { get; set; }

    [JsonPropertyName("metadataRevision")]
    public long? MetadataRevision { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("details")]
    public IReadOnlyDictionary<string, string> Details { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Arguments for <c>honua_publish_result</c> (geospatial-mcp <c>publish_result</c>,
/// #2482): promote a completed analysis job's materialized feature/table artifact
/// into a hosted service + layer. Per the standard schema only <c>sourceId</c>
/// (the completed job whose result is promoted) is required; the honua extension
/// selectors below name the artifact and target service/layer. <c>targetKind</c>,
/// <c>visibility</c>, and <c>routePrefix</c> mirror the standard's promotion-surface
/// fields — the reference implementation supports the
/// <c>published_service</c> promotion (Analyze / Publish Data); <c>deployment</c>
/// (Build App) is not yet materialized server-side and returns a structured
/// <c>failed_precondition</c>.
/// </summary>
internal sealed class McpPublishResultArgument
{
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("artifactId")]
    public string? ArtifactId { get; set; }

    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; set; }

    [JsonPropertyName("layerName")]
    public string? LayerName { get; set; }

    [JsonPropertyName("targetKind")]
    public string? TargetKind { get; set; }

    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }

    [JsonPropertyName("routePrefix")]
    public string? RoutePrefix { get; set; }
}

/// <summary>
/// Output for <c>honua_publish_result</c>. Projects the same canonical
/// <c>OperationHandle</c> shape as <c>honua_publish_service</c> (the promotion
/// routes through the shared <c>service.publish</c> operation): a
/// <c>Completed</c> promotion carries <c>serviceId</c> + <c>layerId</c> the agent
/// chains straight into <c>honua_query_features</c> / <c>honua_render_map</c>; a
/// <c>RequiresApproval</c> outcome carries the approval lane; a <c>Queued</c>
/// outcome carries the durable job id; a <c>Denied</c> outcome carries the
/// reason. <c>sourceJobId</c> / <c>artifactId</c> echo the promoted result
/// (#2482).
/// </summary>
internal sealed class McpPublishResultOutput
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; set; }

    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("handleId")]
    public string HandleId { get; set; } = string.Empty;

    [JsonPropertyName("sourceJobId")]
    public string? SourceJobId { get; set; }

    [JsonPropertyName("artifactId")]
    public string? ArtifactId { get; set; }

    [JsonPropertyName("serviceUri")]
    public string? ServiceUri { get; set; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("layerId")]
    public string? LayerId { get; set; }

    [JsonPropertyName("metadataRevision")]
    public long? MetadataRevision { get; set; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    [JsonPropertyName("approvalLane")]
    public string? ApprovalLane { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Arguments for <c>honua_ingest_dataset</c>: load a small inline CSV or
/// GeoJSON dataset into the catalog database through the canonical
/// <c>IFileImportService</c> pipeline so it can be published with
/// <c>honua_publish_service</c>.
/// </summary>
internal sealed class McpIngestDatasetArgument
{
    /// <summary>Inline data format: "csv" or "geojson".</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>The dataset content, inline (4 MB cap).</summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    /// <summary>Target dataset/table name.</summary>
    [JsonPropertyName("datasetName")]
    public string? DatasetName { get; set; }

    /// <summary>Spatial reference (SRID/WKID) of the input coordinates. Defaults to 4326.</summary>
    [JsonPropertyName("sourceSrid")]
    public int? SourceSrid { get; set; }

    /// <summary>CSV only: explicit longitude (X) column name.</summary>
    [JsonPropertyName("longitudeColumn")]
    public string? LongitudeColumn { get; set; }

    /// <summary>CSV only: explicit latitude (Y) column name.</summary>
    [JsonPropertyName("latitudeColumn")]
    public string? LatitudeColumn { get; set; }

    /// <summary>CSV only: address column geocoded server-side into point geometry.</summary>
    [JsonPropertyName("addressColumn")]
    public string? AddressColumn { get; set; }
}

/// <summary>
/// A per-row issue surfaced by <c>honua_ingest_dataset</c> (for example a CSV
/// address that failed to geocode, imported without geometry).
/// </summary>
internal sealed class McpIngestRowError
{
    /// <summary>1-based data row (header excluded), when known.</summary>
    [JsonPropertyName("row")]
    public int? Row { get; set; }

    /// <summary>Stable machine-readable issue code (e.g. import.address_geocode_failed).</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable issue message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Offending field/column name, when known.</summary>
    [JsonPropertyName("field")]
    public string? Field { get; set; }
}

/// <summary>
/// Output for <c>honua_ingest_dataset</c>. A successful ingest returns the
/// connectionId/schema/table triple (plus geometry column and primary key)
/// that chains directly into <c>honua_publish_service</c>; per-row issues ride
/// alongside without failing the ingest.
/// </summary>
internal sealed class McpIngestDatasetOutput
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("datasetName")]
    public string DatasetName { get; set; } = string.Empty;

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; set; }

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("table")]
    public string? Table { get; set; }

    [JsonPropertyName("srid")]
    public int? Srid { get; set; }

    [JsonPropertyName("geometryColumn")]
    public string? GeometryColumn { get; set; }

    [JsonPropertyName("primaryKey")]
    public string? PrimaryKey { get; set; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];

    [JsonPropertyName("rowErrors")]
    public IReadOnlyList<McpIngestRowError> RowErrors { get; set; } = [];

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Arguments for <c>honua_create_map_package</c>: author a MapPackage from a
/// prompt through the canonical map-generation pipeline (#1951). The standard
/// geospatial-mcp composition selectors (<c>templateId</c>, <c>styleId</c>,
/// <c>themeId</c>) are woven into the generation prompt as explicit guidance.
/// All fields are optional in the published schema (the standard schema marks
/// none required); a missing <c>prompt</c> is reported as a structured
/// <c>invalid_argument</c> at invocation time.
/// </summary>
internal sealed class McpCreateMapPackageArgument
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("templateId")]
    public string? TemplateId { get; set; }

    [JsonPropertyName("styleId")]
    public string? StyleId { get; set; }

    [JsonPropertyName("themeId")]
    public string? ThemeId { get; set; }
}

/// <summary>
/// Arguments for <c>honua_create_app_package</c>: author an AppPackage from a
/// prompt through the canonical app-generation pipeline (#1951). The standard
/// geospatial-mcp composition fields (<c>templateId</c>, <c>targetSdk</c>,
/// <c>mapPackageId</c>, <c>boundArtifactIds</c>) are woven into the generation
/// prompt as explicit guidance.
/// </summary>
internal sealed class McpCreateAppPackageArgument
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("templateId")]
    public string? TemplateId { get; set; }

    [JsonPropertyName("targetSdk")]
    public string? TargetSdk { get; set; }

    [JsonPropertyName("mapPackageId")]
    public string? MapPackageId { get; set; }

    [JsonPropertyName("boundArtifactIds")]
    public IReadOnlyList<string>? BoundArtifactIds { get; set; }
}

/// <summary>
/// Canonical-object plan wire shape consumed by MCP plan tools.
/// Maps directly to <see cref="Honua.Core.Features.Geoprocessing.Domain.AnalysisPlan"/>.
/// </summary>
internal sealed class McpPlanInput
{
    [JsonPropertyName("planId")]
    public string? PlanId { get; set; }

    [JsonPropertyName("intentId")]
    public string? IntentId { get; set; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<McpPlanStepInput>? Steps { get; set; }

    [JsonPropertyName("outputs")]
    public IReadOnlyList<string>? Outputs { get; set; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string>? Warnings { get; set; }
}

internal sealed class McpPlanStepInput
{
    [JsonPropertyName("stepId")]
    public string? StepId { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("processId")]
    public string? ProcessId { get; set; }

    [JsonPropertyName("inputs")]
    public IReadOnlyDictionary<string, string>? Inputs { get; set; }

    [JsonPropertyName("dependsOn")]
    public IReadOnlyList<string>? DependsOn { get; set; }
}

// -----------------------------------------------------------------------
// Tool output shapes (serialized into structuredContent of the call result)
// -----------------------------------------------------------------------

/// <summary>
/// Output for <c>honua_validate_plan</c>.
/// </summary>
internal sealed class McpValidatePlanOutput
{
    [JsonPropertyName("isExecutable")]
    public bool IsExecutable { get; set; }

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; set; }

    [JsonPropertyName("violations")]
    public IReadOnlyList<McpValidationViolation> Violations { get; set; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];
}

/// <summary>
/// Output for <c>honua_dry_run_plan</c>.
/// </summary>
internal sealed class McpDryRunOutput
{
    [JsonPropertyName("estimatedDurationSeconds")]
    public double EstimatedDurationSeconds { get; set; }

    [JsonPropertyName("estimatedArtifacts")]
    public IReadOnlyList<string> EstimatedArtifacts { get; set; } = [];

    [JsonPropertyName("sideEffects")]
    public IReadOnlyList<string> SideEffects { get; set; } = [];
}

/// <summary>
/// Output for <c>honua_execute_plan</c>.
/// </summary>
internal sealed class McpExecuteOutput
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;
}

/// <summary>
/// Output for <c>honua_cancel_job</c>.
/// </summary>
internal sealed class McpCancelJobOutput
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("cancellationRequested")]
    public bool CancellationRequested { get; set; }
}

/// <summary>
/// Envelope for stub tools returning <c>not_implemented</c> with enough
/// information for operators to understand the contract and the unblock path.
/// </summary>
internal sealed class McpNotImplementedOutput
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "not_implemented";

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = string.Empty;

    [JsonPropertyName("blockedBy")]
    public string BlockedBy { get; set; } = string.Empty;

    [JsonPropertyName("contract")]
    public string Contract { get; set; } = string.Empty;

    [JsonPropertyName("nextSteps")]
    public IReadOnlyList<string> NextSteps { get; set; } = [];
}

/// <summary>
/// Structured envelope emitted in <see cref="McpToolsCallResult.StructuredContent"/>
/// when a tool execution fails (auth, validation, approval, domain error). Per
/// MCP 2025-03-26 tool errors surface inside <c>result</c> with
/// <c>isError: true</c>, not as JSON-RPC protocol errors, so clients can still
/// read the tool-level payload for recovery hints.
/// </summary>
internal sealed class McpToolErrorOutput
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "error";

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("requiresReauthentication")]
    public bool? RequiresReauthentication { get; set; }

    [JsonPropertyName("approvalRequired")]
    public bool? ApprovalRequired { get; set; }

    [JsonPropertyName("policyRef")]
    public string? PolicyRef { get; set; }

    [JsonPropertyName("conflictingJobId")]
    public string? ConflictingJobId { get; set; }

    [JsonPropertyName("retryable")]
    public bool? Retryable { get; set; }

    [JsonPropertyName("violations")]
    public IReadOnlyList<McpValidationViolation>? Violations { get; set; }

    [JsonPropertyName("error")]
    public McpGeoprocessingError? Error { get; set; }
}
