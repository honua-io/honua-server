// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Ai.Protocols.Mcp.Models;

// -----------------------------------------------------------------------
// Tool input shapes (arguments passed via tools/call)
// -----------------------------------------------------------------------

internal sealed class McpPlanArgument
{
    [JsonPropertyName("plan")]
    public McpPlanInput? Plan { get; set; }
}

internal sealed class McpExecutePlanArgument
{
    [JsonPropertyName("plan")]
    public McpPlanInput? Plan { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

}

internal sealed class McpCancelJobArgument
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }
}

internal sealed class McpDiagnoseJobArgument
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    [JsonPropertyName("events")]
    public int? Events { get; set; }

    [JsonPropertyName("eventCursor")]
    public string? EventCursor { get; set; }
}

internal sealed class McpJobEvidenceArgument
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    [JsonPropertyName("events")]
    public int? Events { get; set; }

    [JsonPropertyName("eventCursor")]
    public string? EventCursor { get; set; }
}

internal sealed class McpOpsFindingsArgument
{
    [JsonPropertyName("findingId")]
    public string? FindingId { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("rule")]
    public string? Rule { get; set; }
}

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

    [JsonPropertyName("parameterOverrides")]
    public Dictionary<string, string>? ParameterOverrides { get; set; }

    [JsonPropertyName("backupId")]
    public string? BackupId { get; set; }
}

internal sealed class McpProposeFindingArgument
{
    [JsonPropertyName("findingId")]
    public string? FindingId { get; set; }

    [JsonPropertyName("candidateId")]
    public string? CandidateId { get; set; }
}

internal sealed class McpDeployMutationArgument
{
    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    [JsonPropertyName("desiredRevision")]
    public string? DesiredRevision { get; set; }

    [JsonPropertyName("currentRevision")]
    public string? CurrentRevision { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}

internal sealed class McpMetadataReleaseMutationArgument
{
    [JsonPropertyName("packageId")]
    public string? PackageId { get; set; }

    [JsonPropertyName("targetEnvironment")]
    public string? TargetEnvironment { get; set; }

    [JsonPropertyName("resourceSemanticId")]
    public string? ResourceSemanticId { get; set; }

    [JsonPropertyName("newFieldName")]
    public string? NewFieldName { get; set; }

    [JsonPropertyName("newFieldType")]
    public string? NewFieldType { get; set; }

    [JsonPropertyName("dataPopulateWorkloadId")]
    public string? DataPopulateWorkloadId { get; set; }

    [JsonPropertyName("scriptId")]
    public string? ScriptId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}

internal sealed class McpPlatformReleaseConvergenceArgument
{
    [JsonPropertyName("targetVersion")]
    public string? TargetVersion { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}

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

    [JsonPropertyName("supportedKinds")]
    public string[]? SupportedKinds { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("targets")]
    public McpConvergenceTargetOutput[]? Targets { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("declaredVersion")]
    public string? DeclaredVersion { get; set; }

    [JsonPropertyName("targetVersion")]
    public string? TargetVersion { get; set; }

    [JsonPropertyName("servingArtifactReference")]
    public string? ServingArtifactReference { get; set; }

    [JsonPropertyName("isCoVersioned")]
    public bool? IsCoVersioned { get; set; }
}

internal sealed class McpConvergenceTargetOutput
{
    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class McpSupportedOperationKindsOutput
{
    [JsonPropertyName("supportedKinds")]
    public IReadOnlyList<string> SupportedKinds { get; set; } = [];
}

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

    [JsonPropertyName("folderId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MetadataV2FolderSelection FolderId { get; set; }

    [JsonPropertyName("publicName")]
    public string? PublicName { get; set; }

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

internal sealed class McpPublishServiceOutput
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; set; }

    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("operationInstanceId")]
    public string OperationInstanceId { get; set; } = string.Empty;

    [JsonPropertyName("handleId")]
    public string HandleId { get; set; } = string.Empty;

    [JsonPropertyName("durable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Durable { get; set; }

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; set; }

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("auditId")]
    public string? AuditId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("authorizationOutcome")]
    public string? AuthorizationOutcome { get; set; }

    [JsonPropertyName("policyOutcome")]
    public string? PolicyOutcome { get; set; }

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

    [JsonPropertyName("details")]
    public IReadOnlyDictionary<string, string> Details { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("resourceIds")]
    public IReadOnlyDictionary<string, string> ResourceIds { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("evidenceRefs")]
    public IReadOnlyList<string> EvidenceRefs { get; set; } = [];
}

internal sealed class McpOperationToolOutput
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; set; }

    [JsonPropertyName("deterministic")]
    public bool Deterministic { get; set; }

    // Non-deterministic or side-effecting operations are never cached, so this flag is always false for them.
    [JsonPropertyName("cacheHit")]
    public bool CacheHit { get; set; }

    // The cache key is an opaque equality token and must not be parsed.
    [JsonPropertyName("cacheKey")]
    public string? CacheKey { get; set; }

    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("operationInstanceId")]
    public string OperationInstanceId { get; set; } = string.Empty;

    [JsonPropertyName("handleId")]
    public string HandleId { get; set; } = string.Empty;

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; set; }

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("auditId")]
    public string? AuditId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("authorizationOutcome")]
    public string? AuthorizationOutcome { get; set; }

    [JsonPropertyName("policyOutcome")]
    public string? PolicyOutcome { get; set; }

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

    [JsonPropertyName("resourceIds")]
    public IReadOnlyDictionary<string, string> ResourceIds { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("evidenceRefs")]
    public IReadOnlyList<string> EvidenceRefs { get; set; } = [];
}

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

internal sealed class McpPublishResultOutput
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; set; }

    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("operationInstanceId")]
    public string OperationInstanceId { get; set; } = string.Empty;

    [JsonPropertyName("handleId")]
    public string HandleId { get; set; } = string.Empty;

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; set; }

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("auditId")]
    public string? AuditId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("authorizationOutcome")]
    public string? AuthorizationOutcome { get; set; }

    [JsonPropertyName("policyOutcome")]
    public string? PolicyOutcome { get; set; }

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

    [JsonPropertyName("details")]
    public IReadOnlyDictionary<string, string> Details { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("resourceIds")]
    public IReadOnlyDictionary<string, string> ResourceIds { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("evidenceRefs")]
    public IReadOnlyList<string> EvidenceRefs { get; set; } = [];
}

internal sealed class McpIngestDatasetArgument
{
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("datasetName")]
    public string? DatasetName { get; set; }

    [JsonPropertyName("sourceSrid")]
    public int? SourceSrid { get; set; }

    [JsonPropertyName("longitudeColumn")]
    public string? LongitudeColumn { get; set; }

    [JsonPropertyName("latitudeColumn")]
    public string? LatitudeColumn { get; set; }

    [JsonPropertyName("addressColumn")]
    public string? AddressColumn { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpIngestJobArtifactArgument
{
    [JsonPropertyName("intentId")]
    public string? IntentId { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    [JsonPropertyName("resultPackageId")]
    public string? ResultPackageId { get; set; }

    [JsonPropertyName("artifactId")]
    public string? ArtifactId { get; set; }

    [JsonPropertyName("referenceSha256")]
    public string? ReferenceSha256 { get; set; }

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("table")]
    public string? Table { get; set; }

    [JsonPropertyName("targetSrid")]
    public int? TargetSrid { get; set; }

    // The CRS is required only when the producer CRS is unknown and is ignored when the producer CRS is already known.
    [JsonPropertyName("sourceSrid")]
    public int? SourceSrid { get; set; }
}

internal sealed class McpIngestJobArtifactOutput
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("intentId")]
    public string IntentId { get; set; } = string.Empty;

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

    [JsonPropertyName("sourceSrid")]
    public int? SourceSrid { get; set; }

    [JsonPropertyName("crsState")]
    public string? CrsState { get; set; }

    [JsonPropertyName("contentSha256")]
    public string? ContentSha256 { get; set; }

    [JsonPropertyName("geometryColumn")]
    public string? GeometryColumn { get; set; }

    [JsonPropertyName("primaryKey")]
    public string? PrimaryKey { get; set; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

internal sealed class McpIngestRowError
{
    [JsonPropertyName("row")]
    public int? Row { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("field")]
    public string? Field { get; set; }
}

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

// Draft creation performs no inference, so a prompt, provider, or model member is ignored rather than changing the result.
internal sealed class McpCreateMapPackageArgument
{
    [JsonPropertyName("templateId")]
    public string? TemplateId { get; set; }

    [JsonPropertyName("sourceBindings")]
    public IReadOnlyList<McpSourceBindingArgument?>? SourceBindings { get; set; }

    [JsonPropertyName("styleId")]
    public string? StyleId { get; set; }

    [JsonPropertyName("themeId")]
    public string? ThemeId { get; set; }

    [JsonPropertyName("initialView")]
    public McpInitialViewArgument? InitialView { get; set; }
}

internal sealed class McpSourceBindingArgument
{
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("bindingId")]
    public string? BindingId { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("layerId")]
    public string? LayerId { get; set; }

    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; set; }
}

internal sealed class McpInitialViewArgument
{
    [JsonPropertyName("bbox")]
    public IReadOnlyList<double>? Bbox { get; set; }

    [JsonPropertyName("crs")]
    public JsonElement? Crs { get; set; }
}

internal sealed class McpCreateAppPackageArgument
{
    [JsonPropertyName("templateId")]
    public string? TemplateId { get; set; }

    [JsonPropertyName("targetSdk")]
    public string? TargetSdk { get; set; }

    [JsonPropertyName("mapPackageId")]
    public string? MapPackageId { get; set; }

    [JsonPropertyName("boundArtifactIds")]
    public IReadOnlyList<string>? BoundArtifactIds { get; set; }

    [JsonPropertyName("runtimeConfig")]
    public JsonElement? RuntimeConfig { get; set; }
}

internal sealed class McpPackageDraftOutput
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "created";

    [JsonPropertyName("packageId")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("resourceUri")]
    public string ResourceUri { get; set; } = string.Empty;

    [JsonPropertyName("package")]
    public JsonElement? Package { get; set; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<McpPackageDraftFinding> Warnings { get; set; } = [];
}

internal sealed class McpPackageDraftFinding
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

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

internal sealed class McpDryRunOutput
{
    [JsonPropertyName("estimatedDurationSeconds")]
    public double EstimatedDurationSeconds { get; set; }

    // The duration is a placeholder, so callers must not treat 0 as an instant prediction.
    [JsonPropertyName("durationEstimateAvailable")]
    public bool DurationEstimateAvailable { get; set; }

    [JsonPropertyName("estimatedArtifacts")]
    public IReadOnlyList<string> EstimatedArtifacts { get; set; } = [];

    [JsonPropertyName("sideEffects")]
    public IReadOnlyList<string> SideEffects { get; set; } = [];
}

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

internal sealed class McpCancelJobOutput
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("cancellationRequested")]
    public bool CancellationRequested { get; set; }
}

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

    [JsonPropertyName("studioAuthorizationCode")]
    public string? StudioAuthorizationCode { get; set; }

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; set; }

    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }

    [JsonPropertyName("conflictingJobId")]
    public string? ConflictingJobId { get; set; }

    [JsonPropertyName("currentGeneration")]
    public long? CurrentGeneration { get; set; }

    [JsonPropertyName("retryable")]
    public bool? Retryable { get; set; }

    [JsonPropertyName("missingDependency")]
    public string? MissingDependency { get; set; }

    [JsonPropertyName("capability")]
    public string? Capability { get; set; }

    [JsonPropertyName("missingEntitlement")]
    public string? MissingEntitlement { get; set; }

    [JsonPropertyName("remediation")]
    public string? Remediation { get; set; }

    [JsonPropertyName("remediationRef")]
    public string? RemediationRef { get; set; }

    [JsonPropertyName("violations")]
    public IReadOnlyList<McpValidationViolation>? Violations { get; set; }

    [JsonPropertyName("error")]
    public McpGeoprocessingError? Error { get; set; }
}
