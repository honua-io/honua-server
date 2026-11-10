// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal sealed class FeatureServerGeometryServices(IGeometryValidator geometryValidator) : IFeatureServerGeometryServices
{
    private readonly IGeometryValidator _geometryValidator = geometryValidator ?? throw new ArgumentNullException(nameof(geometryValidator));

    public GeometryValidationResult ValidateEsriJson(GeoServicesGeometry? geometry)
        => _geometryValidator.ValidateEsriJson(geometry);

    public GeometryValidationResult ValidateWkb(byte[]? wkb)
        => _geometryValidator.ValidateWkb(wkb);

    public Task<GeometryValidationResult> ValidateCompleteAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => _geometryValidator.ValidateCompleteAsync(wkb, cancellationToken);
}
