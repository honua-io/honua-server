// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// In-memory <see cref="IJobExecutionContext"/> double that records progress,
/// log, and artifact calls so executor behaviour can be asserted without the
/// Redis-backed substrate.
/// </summary>
internal sealed class RecordingJobExecutionContext(string operationId) : IJobExecutionContext
{
    public string OperationId { get; } = operationId;

    public List<(double? Percent, string? Phase)> Progress { get; } = [];

    public List<ExecutionLogEntry> Logs { get; } = [];

    public List<string> Artifacts { get; } = [];

    public Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default)
    {
        Progress.Add((percentComplete, phase));
        return Task.CompletedTask;
    }

    public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
    {
        Logs.Add(entry);
        return Task.CompletedTask;
    }

    public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
    {
        Artifacts.Add(artifactReference);
        return Task.CompletedTask;
    }
}
