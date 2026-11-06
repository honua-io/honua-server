// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface ICrsDetectionService
{
    Task<int?> DetectFromPrjAsync(string prjContent, CancellationToken cancellationToken = default);

    Task<int?> DetectFromWktAsync(string wktContent, CancellationToken cancellationToken = default);

    int? DetectFromEpsgCode(string epsgCode);

    Task<int?> DetectFromGeoJsonCrsAsync(string crsObject, CancellationToken cancellationToken = default);

    Task<int?> DetectFromShapefilePrjAsync(string shapefilePath, CancellationToken cancellationToken = default);

    Task<bool> ValidateSridAsync(int srid, CancellationToken cancellationToken = default);
}
