// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing.Execution;

// Only a dispatcher which has validated the original accepted lease constructs
// this wrapper. Current workspace/source authority is checked again at every
// publication/progress boundary, together with the worker's original claim fence.
internal sealed class AgentManagedProcessExecutionContext(IJobExecutionContext inner,
    ExecutionJobRecord job, IJobExecutionLeaseAuthority authority) : IJobExecutionContext
{
    public string OperationId => inner.OperationId;

    public async Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default)
    {
        await ThrowIfExecutionLeaseLostAsync(cancellationToken).ConfigureAwait(false);
        await inner.ReportProgressAsync(percentComplete, phase, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
    {
        await ThrowIfExecutionLeaseLostAsync(cancellationToken).ConfigureAwait(false);
        await inner.AppendLogAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
    {
        if (!await TryPublishArtifactAsync(artifactReference, cancellationToken).ConfigureAwait(false))
            throw new OperationCanceledException("The original job no longer owns artifact publication.", cancellationToken);
    }

    public async Task<bool> TryPublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
    {
        if (job.Spec.AgentManagedProcessBinding is not { } binding
            || !artifactReference.StartsWith("data:application/geo+json;base64,", StringComparison.Ordinal)
            || !FeatureCollectionArtifact.TryParseDataUri(artifactReference, out _, out _, binding.MaximumOutputBytes)
            || FeatureArtifactSpatialReference.ReadInline(artifactReference, binding.MaximumOutputBytes) is not { State: "known", Srid: 3857 })
            throw AgentManagedBufferContract.Denied();
        await ThrowIfExecutionLeaseLostAsync(cancellationToken).ConfigureAwait(false);
        return await inner.TryPublishArtifactAsync(artifactReference, cancellationToken).ConfigureAwait(false);
    }

    public Task RecordCommittedEffectAsync(string artifactReference, CancellationToken cancellationToken = default)
        => throw AgentManagedBufferContract.Denied();

    public async Task ThrowIfExecutionLeaseLostAsync(CancellationToken cancellationToken = default)
    {
        await inner.ThrowIfExecutionLeaseLostAsync(cancellationToken).ConfigureAwait(false);
        _ = await authority.RevalidateForExecutionAsync(job, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
