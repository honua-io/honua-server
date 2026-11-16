// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Worker.Gdal.Execution;

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

    public ExecutionJobRecord Job { get; }

    public IGeoprocessingOutputObjectStore Store { get; }

    public GeoprocessingOutputStagingOptions StagingOptions { get; }

    public string OperationId => _inner.OperationId;

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
    public Task<bool> TryPublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
        => _inner.TryPublishArtifactAsync(artifactReference, cancellationToken);

    public Task RecordCommittedEffectAsync(string artifactReference, CancellationToken cancellationToken = default)
        => _inner.RecordCommittedEffectAsync(artifactReference, cancellationToken);

    public Task ThrowIfExecutionLeaseLostAsync(CancellationToken cancellationToken = default)
        => _inner.ThrowIfExecutionLeaseLostAsync(cancellationToken);

}
