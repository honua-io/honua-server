// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Provisioner.BuildJobs;

/// <summary>
/// Stable <see cref="Honua.Core.Features.ControlPlane.Domain.ExecutionJobSpec.Parameters"/>
/// keys for the per-area geocoder/router build jobs. The encode/decode contract lives
/// in one place (mirroring <c>TileCacheJobParameterKeys</c>) so the admin submission path
/// and the GP-on-Batch worker that runs the build stay in agreement. Every value is a
/// string because <c>ExecutionJobSpec.Parameters</c> is an opaque string map that survives
/// the durable store and the AWS Batch container-override round-trip.
/// </summary>
public static class ProvisionerBuildJobParameterKeys
{
    /// <summary>
    /// Catalog source id of the feedstock (e.g. <c>census-tiger</c>, <c>osm-geofabrik</c>).
    /// </summary>
    public const string SourceId = "provisioner.source_id";

    /// <summary>
    /// Catalog product id of the feedstock (e.g. <c>addresses</c>, <c>roads</c>).
    /// </summary>
    public const string ProductId = "provisioner.product_id";

    /// <summary>
    /// Raw, round-trippable AREA selector (<c>bbox:...</c> or <c>geoid:...</c>).
    /// </summary>
    public const string Area = "provisioner.area";

    /// <summary>
    /// Published feature layer / PostGIS table the build clips its input from
    /// (the layer the area-import provisioner already loaded).
    /// </summary>
    public const string FeedstockTable = "provisioner.feedstock_table";

    /// <summary>
    /// Target PostGIS schema the feedstock table lives in.
    /// </summary>
    public const string SchemaName = "provisioner.schema_name";

    /// <summary>
    /// Logical name the produced artifact is registered/published under so the
    /// GeocodeServer locator (or routing endpoint) can resolve it.
    /// </summary>
    public const string ArtifactName = "provisioner.artifact_name";

    /// <summary>
    /// Object-store key (S3, same pattern as PMTiles) the build writes its artifact to.
    /// </summary>
    public const string ArtifactKey = "provisioner.artifact_key";

    /// <summary>
    /// Geocoder-only: locator artifact kind the build emits (see
    /// <see cref="GeocoderArtifactKinds"/>).
    /// </summary>
    public const string LocatorKind = "provisioner.locator_kind";

    /// <summary>
    /// Router-only: routing-graph artifact kind the build emits (see
    /// <see cref="RouterArtifactKinds"/>).
    /// </summary>
    public const string GraphKind = "provisioner.graph_kind";
}

/// <summary>
/// Locator artifact kinds a geocoder build can emit. A self-hosted Nominatim import
/// bundle is the default because the existing <c>NominatimGeocodeProvider</c> serves the
/// GeocodeServer <c>findAddressCandidates</c>/<c>reverseGeocode</c>/<c>suggest</c>
/// operations by pointing its <c>BaseUrl</c> at a Nominatim instance built from that
/// bundle — no per-credit external geocoder required.
/// </summary>
public static class GeocoderArtifactKinds
{
    /// <summary>
    /// An <c>.osm.pbf</c> address extract a self-hosted Nominatim imports
    /// (<c>nominatim import</c>), which the <c>NominatimGeocodeProvider</c> then serves.
    /// </summary>
    public const string NominatimPbf = "nominatim-pbf";
}

/// <summary>
/// Routing-graph artifact kinds a router build can emit. The pgRouting topology is the
/// default because the existing <c>PgRoutingProvider</c> solves routes/service areas with
/// <c>pgr_dijkstra</c>/<c>pgr_drivingDistance</c> over an osm2pgrouting-style
/// <c>ways</c> / <c>ways_vertices_pgr</c> topology.
/// </summary>
public static class RouterArtifactKinds
{
    /// <summary>
    /// An osm2pgrouting-style <c>ways</c> / <c>ways_vertices_pgr</c> topology (delivered
    /// as a PostGIS dump in object store) that <c>PgRoutingProvider</c> solves over.
    /// </summary>
    public const string PgRoutingTopology = "pgrouting-topology";
}
