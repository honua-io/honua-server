// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.TestKit.Providers;

/// <summary>
/// Builds the shared Metadata v2 graph used by the provider-http-smoke fixtures
/// (<see cref="DuckDbProviderWebAppFixture"/>, <see cref="MySqlProviderWebAppFixture"/>,
/// <see cref="SqlServerProviderWebAppFixture"/>). Every fixture seeds the exact same
/// 5-row "parcels" dataset (see <see cref="ProviderSmokeData"/>) so the smoke test
/// assertions (row counts, filter results, extents) are identical across providers —
/// only the physical storage and Metadata v2 storage-binding connection differ.
/// </summary>
/// <remarks>
/// One resource is published three times, once per protocol family, mirroring the
/// convention <c>WebAppFixtureMetadataV2Mixin.BuildDefaultTestGraph</c> uses for the
/// Postgres-backed default test graph: a dedicated <see cref="MetadataV2Service"/> per
/// protocol (GeoServices FeatureServer, OGC API Features, OData) each with a single
/// publication of the protocol-appropriate <see cref="MetadataV2PublicationType"/>. OGC
/// API Tiles is layered onto the OGC API Features service since it addresses the same
/// collection resource (<c>/ogc/tiles/collections/{collectionId}/tiles/...</c>).
/// </remarks>
public static class ProviderSmokeGraph
{
    /// <summary>
    /// Layer id / OGC API Features collection id used by every smoke fixture. 1, not 0:
    /// <c>MySqlOptionsValidator</c> requires a positive layer <c>Id</c> (DuckDB's validator
    /// has no such floor), so a shared id across all three provider fixtures must satisfy
    /// the stricter constraint.
    /// </summary>
    public const int LayerId = 1;

    /// <summary>Service name used for the GeoServices FeatureServer smoke publication.</summary>
    public const string ServiceName = "smoke";

    /// <summary>Resource id of the published "parcels" dataset.</summary>
    public const string ResourceId = "res-smoke";

    private const string BindingId = "binding-smoke";

    /// <summary>
    /// Builds the Metadata v2 graph for a provider-smoke fixture.
    /// </summary>
    /// <param name="locator">
    /// Storage binding locator (e.g. <c>"parcels"</c> for DuckDB/MySql static-config layers,
    /// or <c>"dbo.parcels_xxx"</c> for a SQL Server table resolved dynamically through the
    /// storage mapping).
    /// </param>
    /// <param name="connectionId">
    /// Metadata v2 connection id routing this binding to a secondary provider (SQL Server).
    /// Left <see langword="null"/> for primary-provider fixtures (DuckDB/MySql), where the
    /// binding falls back to the router's default provider (the primary <c>DataSource:Provider</c>).
    /// </param>
    /// <param name="connectionProvider">
    /// Canonical provider name for the connection node (e.g. <c>"sqlserver"</c>). Only used
    /// when <paramref name="connectionId"/> is set.
    /// </param>
    public static MetadataV2Graph Build(string locator, string? connectionId = null, string? connectionProvider = null)
    {
        var fields = new[]
        {
            new MetadataV2Field
            {
                // Named "objectid" (not just PK-mapped as "id") to sidestep a real,
                // pre-existing bug found while building this suite:
                // Honua.Core.Features.Query.QueryProcessor.OptimizeQuery hardcodes
                // FieldNames.ObjectId ("objectid") as the default deterministic-pagination
                // sort field instead of resolving the resource's actual primary-id field
                // (resource.FindPrimaryIdField()?.Name), so any resource whose PK field is
                // named anything else fails every paged query with a SQL binder error.
                // Tracked as honua-server#2964; every existing seed/schema in this repo
                // happens to already name its PK field "objectid", which is why this has
                // never surfaced before.
                Name = "objectid",
                Type = MetadataV2FieldType.BigInteger,
                Nullable = false,
                SemanticRoles = ["id.primary"],
                Description = "Object id",
            },
            new MetadataV2Field
            {
                Name = "geom",
                Type = MetadataV2FieldType.Geometry,
                Nullable = false,
                SemanticRoles = ["geometry.primary", "geometry"],
                Description = "Geometry",
            },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Description = "Parcel name" },
            new MetadataV2Field { Name = "area", Type = MetadataV2FieldType.Double, Nullable = true, Description = "Parcel area" },
            new MetadataV2Field { Name = "type", Type = MetadataV2FieldType.String, Nullable = true, Description = "Parcel type" },
        };

        var spatial = new MetadataV2ResourceSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            GeometryType = MetadataV2GeometryType.Point,
            PrimaryGeometryField = "geom",
            Bbox = new MetadataV2Bbox
            {
                West = ProviderSmokeData.MinLongitude,
                South = ProviderSmokeData.MinLatitude,
                East = ProviderSmokeData.MaxLongitude,
                North = ProviderSmokeData.MaxLatitude,
            },
            SupportedCrs = [MetadataV2SpatialReference.Wgs84],
        };

        var bindingOptions = new Dictionary<string, JsonElement>
        {
            ["primaryKeyColumn"] = JsonSerializer.SerializeToElement("objectid"),
            ["geometryColumn"] = JsonSerializer.SerializeToElement("geom"),
        };

        var builder = new Honua.TestKit.Infrastructure.TestMetadataV2GraphBuilder();

        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            builder.AddConnection(connectionId, "smoke-connection", MetadataV2ConnectionType.Database, connectionProvider);
        }

        var layerIdText = LayerId.ToString(CultureInfo.InvariantCulture);
        var anonymous = new AccessPolicy { AllowAnonymous = true };

        // A single service, not one-per-protocol: ResourceValidator.ValidateServiceV2Async
        // resolves a service by name via snapshot.Index.ServicesByName, a dictionary keyed
        // by MetadataV2Service.Name — only ONE MetadataV2Service record can ever be reachable
        // per name (the default test graph's "svc-test" carries protocols: All for exactly
        // this reason; its same-named svc-test-feature/svc-test-map/svc-test-stac siblings
        // are never resolved by name-based lookup). Splitting the smoke service across
        // several same-named MetadataV2Service records here would make every protocol but
        // the one on the first-indexed record 404 with "<protocol> is not enabled for this
        // service" — found the hard way while building this fixture (honua-server#2947).
        builder
            .AddResource(
                ResourceId,
                "parcels",
                MetadataV2ResourceType.FeatureDataset,
                fields: fields,
                accessPolicy: anonymous,
                spatial: spatial)
            .AddStorageBinding(
                BindingId,
                ResourceId,
                locator,
                connectionId: connectionId,
                storageLayerId: LayerId,
                options: bindingOptions)
            .AddService(
                "svc-smoke",
                ServiceName,
                route: "/ogc/features",
                protocols: MetadataV2ServiceProtocols.All,
                accessPolicy: anonymous)
            // OGC API Features + OGC API Tiles (Tiles addresses
            // /ogc/tiles/collections/{collectionId}/tiles/... for the same collection id).
            .AddPublication(
                "pub-smoke-ogc",
                serviceId: "svc-smoke",
                resourceId: ResourceId,
                layerIndex: LayerId,
                storageBindingId: BindingId,
                serviceLocalId: layerIdText,
                publicationType: MetadataV2PublicationType.OgcCollection)
            // GeoServices REST FeatureServer.
            .AddPublication(
                "pub-smoke-feature",
                serviceId: "svc-smoke",
                resourceId: ResourceId,
                layerIndex: LayerId,
                storageBindingId: BindingId,
                serviceLocalId: layerIdText,
                publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            // OData. Query dispatch for /odata/Layers({layerId})/Features addresses the
            // primary provider's IFeatureReader directly by numeric layer id — this
            // publication only feeds $metadata/schema generation.
            .AddPublication(
                "pub-smoke-odata",
                serviceId: "svc-smoke",
                resourceId: ResourceId,
                layerIndex: LayerId,
                storageBindingId: BindingId,
                serviceLocalId: layerIdText,
                publicationType: MetadataV2PublicationType.ODataEntitySet);

        return builder.Build();
    }
}
