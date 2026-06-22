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

    [JsonPropertyName("message")]
    public string? Message { get; set; }
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
}
