// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Server.Features.FeatureServer.Models;

namespace Honua.Server.Features.FeatureServer.Services;

internal sealed class FeatureServerGeometryServices(IGeometryValidator geometryValidator) : IFeatureServerGeometryServices
{
    private readonly IGeometryValidator _geometryValidator = geometryValidator ?? throw new ArgumentNullException(nameof(geometryValidator));

    public GeometryValidationResult ValidateEsriJson(GeoServicesGeometry? geometry)
        => _geometryValidator.ValidateEsriJson(geometry);

    public GeometryValidationResult ValidateWkb(byte[]? wkb)
        => _geometryValidator.ValidateWkb(wkb);
}
