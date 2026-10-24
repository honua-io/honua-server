// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal interface IFeatureServerGeometryServices
{
    GeometryValidationResult ValidateEsriJson(GeoServicesGeometry? geometry);

    GeometryValidationResult ValidateWkb(byte[]? wkb);

    Task<GeometryValidationResult> ValidateCompleteAsync(byte[] wkb, CancellationToken cancellationToken = default);
}
