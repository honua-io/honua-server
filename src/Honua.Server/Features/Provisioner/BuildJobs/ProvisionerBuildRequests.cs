// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Provisioner.BuildJobs;

/// <summary>
/// Request to build a per-area <b>geocoder/locator</b> artifact from an imported address
/// feedstock layer. The build clips the feedstock to <see cref="Area"/> and emits a
/// locator artifact (default: a Nominatim-importable <c>.osm.pbf</c>) that the
/// GeocodeServer serves through the existing <c>NominatimGeocodeProvider</c>.
/// </summary>
public sealed record GeocoderBuildRequest
{
    /// <summary>Catalog source id of the address feedstock (e.g. <c>census-tiger</c>).</summary>
    public required string SourceId { get; init; }

    /// <summary>Catalog product id of the address feedstock (e.g. <c>addresses</c>).</summary>
    public required string ProductId { get; init; }

    /// <summary>The validated area the locator covers.</summary>
    public required ProvisionerArea Area { get; init; }

    /// <summary>Published feature layer / PostGIS table the build clips its input from.</summary>
    public required string FeedstockTable { get; init; }

    /// <summary>Target PostGIS schema. Defaults to <c>public</c>.</summary>
    public string SchemaName { get; init; } = "public";

    /// <summary>Logical locator name the GeocodeServer resolves (e.g. <c>maui</c>).</summary>
    public required string ArtifactName { get; init; }

    /// <summary>Object-store key the build writes the locator artifact to.</summary>
    public required string ArtifactKey { get; init; }

    /// <summary>Locator artifact kind. Defaults to <see cref="GeocoderArtifactKinds.NominatimPbf"/>.</summary>
    public string LocatorKind { get; init; } = GeocoderArtifactKinds.NominatimPbf;
}

/// <summary>
/// Request to build a per-area <b>routing graph</b> artifact from an imported road-network
/// feedstock layer. The build clips the network to <see cref="Area"/> and emits a routing
/// graph (default: an osm2pgrouting-style pgRouting topology) that a routing endpoint
/// solves over through the existing <c>PgRoutingProvider</c>.
/// </summary>
public sealed record RouterBuildRequest
{
    /// <summary>Catalog source id of the road feedstock (e.g. <c>osm-geofabrik</c>, <c>census-tiger</c>).</summary>
    public required string SourceId { get; init; }

    /// <summary>Catalog product id of the road feedstock (e.g. <c>roads</c>).</summary>
    public required string ProductId { get; init; }

    /// <summary>The validated area the routing graph covers.</summary>
    public required ProvisionerArea Area { get; init; }

    /// <summary>Published feature layer / PostGIS table the build clips its input from.</summary>
    public required string FeedstockTable { get; init; }

    /// <summary>Target PostGIS schema. Defaults to <c>public</c>.</summary>
    public string SchemaName { get; init; } = "public";

    /// <summary>Logical routing-network name the routing endpoint resolves (e.g. <c>maui</c>).</summary>
    public required string ArtifactName { get; init; }

    /// <summary>Object-store key the build writes the routing-graph artifact to.</summary>
    public required string ArtifactKey { get; init; }

    /// <summary>Routing-graph artifact kind. Defaults to <see cref="RouterArtifactKinds.PgRoutingTopology"/>.</summary>
    public string GraphKind { get; init; } = RouterArtifactKinds.PgRoutingTopology;
}
