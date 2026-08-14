// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Decorates the durable <see cref="IJobExecutionContext"/> with the staged-output
/// publication seam (#3089). <see cref="GdalDispatchJobExecutor"/> installs it when a
/// <see cref="IGeoprocessingOutputObjectStore"/> is registered, so per-process
/// executors need no staging awareness of their own:
/// <see cref="GdalArtifactPublisher"/> pattern-matches the context to obtain the
/// store, the durable job record (for the attempt fence and output naming), and the
/// staging options. All <see cref="IJobExecutionContext"/> members delegate unchanged.
/// </summary>
internal sealed class GdalStagedOutputContext : IJobExecutionContext
{
    private readonly IJobExecutionContext _inner;
    private int _publishedOutputIndex;

    public GdalStagedOutputContext(
        IJobExecutionContext inner,
        ExecutionJobRecord job,
        IGeoprocessingOutputObjectStore store,
        GeoprocessingOutputStagingOptions stagingOptions)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Job = job ?? throw new ArgumentNullException(nameof(job));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        StagingOptions = stagingOptions ?? throw new ArgumentNullException(nameof(stagingOptions));
    }

    /// <summary>Durable record of the executing attempt (attempt fence + output naming).</summary>
    public ExecutionJobRecord Job { get; }

    /// <summary>Registered staged-output object store.</summary>
    public IGeoprocessingOutputObjectStore Store { get; }

    /// <summary>Staging configuration snapshot for this execution.</summary>
    public GeoprocessingOutputStagingOptions StagingOptions { get; }

    public string OperationId => _inner.OperationId;

    /// <summary>Reserves the next zero-based logical output slot for this execution.</summary>
    public int NextOutputIndex() => _publishedOutputIndex++;

    public Task ReportProgressAsync(
        double? percentComplete,
        string? phase,
        CancellationToken cancellationToken = default)
        => _inner.ReportProgressAsync(percentComplete, phase, cancellationToken);

    public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        => _inner.AppendLogAsync(entry, cancellationToken);

    public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
        => _inner.PublishArtifactAsync(artifactReference, cancellationToken);
}
