// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Services;

/// <summary>
/// S1 placeholder implementation for durable resource kinds
/// (<see cref="SpecResourceKind.Dataset"/>, <see cref="SpecResourceKind.Service"/>,
/// <see cref="SpecResourceKind.App"/>). Read calls return <c>null</c> so the
/// planner can surface missing resources as <c>unknown-service</c>; writes
/// raise <see cref="SpecExecutionException"/> with
/// <see cref="SpecDiagnosticCodes.SpecKindNotInS1"/>.
/// </summary>
internal sealed class ReservedSpecResourceStateStore : ISpecResourceStateStore
{
    public ReservedSpecResourceStateStore(SpecResourceKind kind)
    {
        Kind = kind;
    }

    public SpecResourceKind Kind { get; }

    public Task<SpecResourceState?> ReadCurrentAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<SpecResourceState?>(null);
    }

    public Task UpsertAsync(SpecResourceState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        throw BuildException(state.Name);
    }

    public Task DestroyAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        throw BuildException(resourceName);
    }

    private SpecExecutionException BuildException(string name) =>
        new(
            SpecDiagnosticCodes.SpecKindNotInS1,
            $"Durable state writes for '{Kind}' ({name}) are not available in the current release.",
            nodeId: null,
            remedy: "Restructure the spec to use a compute/report node for this release, or wait for S2 durable resources.");
}
