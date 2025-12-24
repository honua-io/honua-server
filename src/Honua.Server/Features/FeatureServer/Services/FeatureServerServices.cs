// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Aggregates FeatureServer services to keep handler dependencies minimal.
/// </summary>
internal sealed class FeatureServerServices(
    IQueryFormatter queryFormatter,
    IFeatureQueryValidator queryValidator,
    Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter)
{
    public IQueryFormatter QueryFormatter { get; } = queryFormatter ?? throw new ArgumentNullException(nameof(queryFormatter));
    public IFeatureQueryValidator QueryValidator { get; } = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
    public Honua.Server.Features.Infrastructure.Services.IGeometryConverter GeometryConverter { get; } =
        geometryConverter ?? throw new ArgumentNullException(nameof(geometryConverter));
}
