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

        return backends.FirstOrDefault(backend =>
            string.Equals(backend.BackendName, backendName, StringComparison.Ordinal) &&
            backend.TargetKind == targetKind);
    }
}
