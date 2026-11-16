// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record DagSourceRequest
{
    public int? LayerId { get; init; }

    public string? ServiceUrl { get; init; }

    public int? GeoServicesLayerId { get; init; }

    public string? CollectionId { get; init; }

    public string? Where { get; init; }

    public string? Bbox { get; init; }

    public string? ObjectIds { get; init; }

    public string? Geometry { get; init; }

    public string? GeometryType { get; init; }

    public string? InSr { get; init; }

    public string? SpatialRel { get; init; }

    public string? Time { get; init; }

    public string? TimeRelation { get; init; }

    public bool HasCanonicalSelectors =>
        !string.IsNullOrWhiteSpace(Geometry) || !string.IsNullOrWhiteSpace(Time);

    public string? OutFields { get; init; }

    public int? OutputSrid { get; init; }

    public int PageSize { get; init; } = 1000;

    public string? Since { get; init; }

    public string? WatermarkField { get; init; }

    public int? MaxPages { get; init; }

    public int TimeoutSeconds { get; init; } = 120;

    public GeoservicesCredentialDescriptor? GeoServicesCredentials { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? PostgisConnectionString { get; init; }

    public string? PostgisSchema { get; init; }

    public string? PostgisTable { get; init; }

    public string? PostgisGeometryColumn { get; init; }
}
