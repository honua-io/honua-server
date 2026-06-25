// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing.LocalRunner;

/// <summary>
/// Structured outcome of a single <see cref="GeoprocessingLocalRunner"/> invocation
/// (issue #2123): terminal status, the published artifact reference(s), the structured
/// logs the executor emitted (including, for GDAL ops, the actual CLI command), any
/// warnings, an error message on failure, and wall-clock timing.
/// </summary>
public sealed record LocalRunResult
{
    /// <summary>The process id that was invoked.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Terminal status reported by the executor.</summary>
    public required ExecutionJobStatus Status { get; init; }

    /// <summary>Whether the run succeeded.</summary>
    public bool Succeeded => Status == ExecutionJobStatus.Succeeded;

    /// <summary>Classified error message when the run failed; otherwise <c>null</c>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Artifact references the executor published, in order.</summary>
    public IReadOnlyList<string> Artifacts { get; init; } = [];

    /// <summary>Structured log entries emitted during execution, in order.</summary>
    public IReadOnlyList<ExecutionLogEntry> Logs { get; init; } = [];

    /// <summary>Warnings collected during execution.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Wall-clock duration of the executor invocation.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Builds the failed result returned when the requested process id is not
    /// registered with the runner.
    /// </summary>
    /// <param name="processId">The unknown process id.</param>
    /// <param name="supportedProcessIds">Comma-separated list of available ids.</param>
    public static LocalRunResult UnknownProcess(string processId, string supportedProcessIds) => new()
    {
        ProcessId = processId,
        Status = ExecutionJobStatus.Failed,
        ErrorMessage =
            $"Process id '{processId}' is not registered with the local runner. "
            + $"Available process ids: {supportedProcessIds}.",
    };
}
