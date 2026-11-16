// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class NoOpCrsDetectionService : ICrsDetectionService
{
    public Task<int?> DetectFromPrjAsync(string prjContent, CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(null);

    public Task<int?> DetectFromWktAsync(string wktContent, CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(null);

    public int? DetectFromEpsgCode(string epsgCode) => null;

    public Task<int?> DetectFromGeoJsonCrsAsync(string crsObject, CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(null);

    public Task<int?> DetectFromShapefilePrjAsync(string shapefilePath, CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(null);

    public Task<bool> ValidateSridAsync(int srid, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
