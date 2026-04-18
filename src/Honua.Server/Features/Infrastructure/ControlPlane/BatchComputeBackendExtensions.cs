// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Resolution helpers for <see cref="IBatchComputeBackend"/> collections, keeping
/// the <c>(BackendName, TargetKind)</c> lookup key consistent across the
/// geoprocessing submit path and the execution-job reconciler.
/// </summary>
internal static class BatchComputeBackendExtensions
{
    /// <summary>
    /// Resolves a backend by its canonical <c>(BackendName, TargetKind)</c> key,
    /// returning null when no backend is registered for the combination.
    /// </summary>
    public static IBatchComputeBackend? Resolve(
        this IEnumerable<IBatchComputeBackend> backends,
        string backendName,
        BatchComputeTargetKind targetKind)
    {
        ArgumentNullException.ThrowIfNull(backends);

        if (string.IsNullOrWhiteSpace(backendName))
        {
            return null;
        }

        foreach (var backend in backends)
        {
            if (string.Equals(backend.BackendName, backendName, StringComparison.Ordinal) &&
                backend.TargetKind == targetKind)
            {
                return backend;
            }
        }

        return null;
    }
}
