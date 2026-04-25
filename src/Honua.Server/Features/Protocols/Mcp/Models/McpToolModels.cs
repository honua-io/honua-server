// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Mcp.Models;

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
