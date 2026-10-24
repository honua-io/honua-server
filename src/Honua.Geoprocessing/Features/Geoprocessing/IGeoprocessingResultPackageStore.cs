// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Durable store for terminal geoprocessing result packages keyed by job identifier.
/// </summary>
internal interface IGeoprocessingResultPackageStore
{
    /// <summary>
    /// Returns the stored result package for <paramref name="jobId"/>, or <c>null</c> when absent.
    /// </summary>
    Task<AnalysisResultPackage?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the result package for <paramref name="jobId"/>.
    /// </summary>
    Task SetAsync(
        string jobId,
        AnalysisResultPackage package,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);
}
