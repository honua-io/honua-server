// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Structured logging for natural-language workflow-package generation.
/// Event ID range: 6650-6699. Telemetry activities use the <c>honua.workflowgen.*</c> shape.
/// </summary>
internal static partial class WorkflowGenerationLog
{
    [LoggerMessage(
        EventId = 6650,
        Level = LogLevel.Information,
        Message = "Workflow generation requested (provider={Provider}, model={Model})")]
    public static partial void GenerationRequested(ILogger logger, string provider, string model);

    [LoggerMessage(
        EventId = 6651,
        Level = LogLevel.Information,
        Message = "Workflow generation produced status {Status} (provider={Provider}, nodes={NodeCount})")]
    public static partial void GenerationProduced(ILogger logger, string status, string provider, int nodeCount);

    [LoggerMessage(
        EventId = 6652,
        Level = LogLevel.Warning,
        Message = "Workflow generation failed (provider={Provider}): {Reason}")]
    public static partial void GenerationFailed(ILogger logger, string provider, string reason);

    [LoggerMessage(
        EventId = 6653,
        Level = LogLevel.Information,
        Message = "Workflow generation repair attempt {Attempt} (provider={Provider}, failures={FailureCount})")]
    public static partial void RepairAttempt(ILogger logger, int attempt, string provider, int failureCount);
}
