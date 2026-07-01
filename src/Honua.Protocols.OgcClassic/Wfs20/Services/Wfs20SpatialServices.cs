// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Feature-local facade that groups the shared geometry/CRS collaborators used by the WFS handler.
/// Paired with <see cref="Wfs20QueryServices"/> and <see cref="Wfs20EditServices"/> so the handler
/// composes a small number of cohesive facades instead of one large aggregate, without changing behavior.
/// </summary>
internal sealed class Wfs20SpatialServices(
    OgcFeaturesGeometryServices geometryServices,
    ICoordinateTransformService coordinateTransformService,
    ICrsRegistry crsRegistry)
{
    internal OgcFeaturesGeometryServices GeometryServices { get; } = geometryServices;

    internal ICoordinateTransformService CoordinateTransformService { get; } = coordinateTransformService;

    internal ICrsRegistry CrsRegistry { get; } = crsRegistry;
}
