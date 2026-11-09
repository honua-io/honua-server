// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal interface IGeoprocessingResultPackageStore
{
    Task<AnalysisResultPackage?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string jobId,
        AnalysisResultPackage package,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);
}
