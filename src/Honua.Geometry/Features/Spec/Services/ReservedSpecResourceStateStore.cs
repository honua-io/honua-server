// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
internal sealed class ReservedSpecResourceStateStore
{
    public ReservedSpecResourceStateStore(SpecResourceKind kind)
    {
        Kind = kind;
    }

    public SpecResourceKind Kind { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Retains the injected instance API after removing its single-implementation interface.")]
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

    private SpecExecutionException BuildException(string name) =>
        new(
            SpecDiagnosticCodes.SpecKindNotInS1,
            $"Durable state writes for '{Kind}' ({name}) are not available in the current release.",
            nodeId: null,
            remedy: "Restructure the spec to use a compute/report node for this release, or wait for S2 durable resources.");
}
