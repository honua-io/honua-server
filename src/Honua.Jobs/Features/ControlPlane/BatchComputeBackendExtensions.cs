// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal static class BatchComputeBackendExtensions
{
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

        var named = backends
            .Where(backend => string.Equals(backend.BackendName, backendName, StringComparison.Ordinal))
            .ToArray();
        var exact = named.FirstOrDefault(backend => backend.TargetKind == targetKind);
        if (exact is not null)
        {
            return exact;
        }

        // The in-process backend launches no workload, so the target kind on its specs carries no
        // routing meaning. Tile-export and topology-rebuild specs (and rows already persisted from
        // them) say LocalProcess while the backend reports KubernetesJob.
        return string.Equals(backendName, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
            ? named.FirstOrDefault()
            : null;
    }
}
