// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing.LocalRunner;

internal sealed class LocalJobExecutionContext(string operationId) : IJobExecutionContext
{
    private readonly List<ExecutionLogEntry> _logs = [];
    private readonly List<string> _artifacts = [];
    private readonly List<LocalRunPhase> _phases = [];

    public string OperationId { get; } = operationId;

    public IReadOnlyList<ExecutionLogEntry> Logs => _logs;

    public IReadOnlyList<string> Artifacts => _artifacts;

    public IReadOnlyList<LocalRunPhase> Phases => _phases;

    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    public Task ReportProgressAsync(
        double? percentComplete,
        string? phase,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(phase))
        {
            _phases.Add(new LocalRunPhase
            {
                Phase = phase,
                PercentComplete = percentComplete,
                Elapsed = _stopwatch.Elapsed,
            });
        }

        return Task.CompletedTask;
    }

    public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _logs.Add(entry);
        return Task.CompletedTask;
    }

    public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactReference);
        _artifacts.Add(artifactReference);
        return Task.CompletedTask;
    }
}
