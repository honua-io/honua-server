// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing.Execution;

/// <summary>Dispatch-owned storage and identity for managed streams; never selected by plan parameters.</summary>
internal sealed class ManagedFeatureStreamContext(
    IJobExecutionContext inner,
    ExecutionJobRecord job,
    IGeoprocessingOutputObjectStore? store,
    IExecutionJobStore? jobs,
    GeoprocessingOutputStagingOptions options,
    IServiceScopeFactory? workspaceScopes = null) : IJobExecutionContext
{
    public ExecutionJobRecord Job { get; } = job;
    public IGeoprocessingOutputObjectStore? Store { get; } = store;
    public IExecutionJobStore? Jobs { get; } = jobs;
    public GeoprocessingOutputStagingOptions Options { get; } = options;
    public IServiceScopeFactory? WorkspaceScopes { get; } = workspaceScopes;
    public string OperationId => inner.OperationId;

    public async Task<string> StageAsync(string path, long count, long bytes, CancellationToken cancellationToken)
    {
        if (Store is null || Jobs is null || !Options.Enabled || Job.AttemptCount < 1
            || bytes > DurableFeatureStream.MaximumBytes || Store.StoreReference != Options.StoreReference)
        {
            throw new InvalidOperationException("Large managed outputs require the configured shared persistent output staging store.");
        }

        await ThrowIfExecutionLeaseLostAsync(cancellationToken).ConfigureAwait(false);
        var key = GeoprocessingOutputObjectKeys.Build(Options.KeyPrefix, Job.OperationId, Job.AttemptCount,
            "features", Guid.NewGuid().ToString("N") + ".ndjson");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var identity = await Store.WriteAsync(key, stream, "application/x-ndjson", cancellationToken).ConfigureAwait(false);
        if (identity.SizeBytes != bytes || identity.Checksum is not { Algorithm: "sha256" } checksum)
        {
            throw new InvalidOperationException("Shared output storage did not produce a complete content identity.");
        }

        return new DurableFeatureStream
        {
            JobId = Job.OperationId,
            Attempt = Job.AttemptCount,
            ObjectKey = key,
            StoreReference = Store.StoreReference,
            Provider = Store.Provider,
            Count = count,
            Bytes = bytes,
            Sha256 = checksum.Value,
        }.Serialize();
    }

    public Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default)
        => inner.ReportProgressAsync(percentComplete, phase, cancellationToken);
    public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        => inner.AppendLogAsync(entry, cancellationToken);
    public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
        => inner.PublishArtifactAsync(artifactReference, cancellationToken);
    public Task<bool> TryPublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
        => inner.TryPublishArtifactAsync(artifactReference, cancellationToken);
    public Task RecordCommittedEffectAsync(string artifactReference, CancellationToken cancellationToken = default)
        => inner.RecordCommittedEffectAsync(artifactReference, cancellationToken);
    public Task ThrowIfExecutionLeaseLostAsync(CancellationToken cancellationToken = default)
        => inner.ThrowIfExecutionLeaseLostAsync(cancellationToken);
}
