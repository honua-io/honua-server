// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing.LocalRunner;

/// <summary>
/// In-memory <see cref="IJobExecutionContext"/> for the headless
/// <see cref="GeoprocessingLocalRunner"/> (issue #2123). Records every progress
/// update, structured log entry, and published artifact reference in order so the
/// runner can surface them to the caller. It performs no I/O and reaches no Redis,
/// job store, or control plane — the runner is fully self-contained.
/// </summary>
internal sealed class LocalJobExecutionContext(string operationId) : IJobExecutionContext
{
    private readonly List<ExecutionLogEntry> _logs = [];
    private readonly List<string> _artifacts = [];

    /// <inheritdoc />
    public string OperationId { get; } = operationId;

    /// <summary>Structured log entries appended during execution, in order.</summary>
    public IReadOnlyList<ExecutionLogEntry> Logs => _logs;

    /// <summary>Artifact references published during execution, in order.</summary>
    public IReadOnlyList<string> Artifacts => _artifacts;

    /// <inheritdoc />
    public Task ReportProgressAsync(
        double? percentComplete,
        string? phase,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _logs.Add(entry);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactReference);
        _artifacts.Add(artifactReference);
        return Task.CompletedTask;
    }
}
