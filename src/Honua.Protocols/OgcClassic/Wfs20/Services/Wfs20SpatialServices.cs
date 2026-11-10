// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal sealed class Wfs20SpatialServices(
    OgcFeaturesGeometryServices geometryServices,
    ICoordinateTransformService coordinateTransformService,
    ICrsRegistry crsRegistry)
{
    internal OgcFeaturesGeometryServices GeometryServices { get; } = geometryServices;

    internal ICoordinateTransformService CoordinateTransformService { get; } = coordinateTransformService;

    internal ICrsRegistry CrsRegistry { get; } = crsRegistry;
}
