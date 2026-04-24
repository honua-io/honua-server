// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Domain;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

internal interface IFeatureServerGeometryServices
{
    GeometryValidationResult ValidateEsriJson(GeoServicesGeometry? geometry);

    GeometryValidationResult ValidateWkb(byte[]? wkb);

    Task<GeometryValidationResult> ValidateCompleteAsync(byte[] wkb, CancellationToken cancellationToken = default);
}
