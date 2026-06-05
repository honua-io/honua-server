// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.TestKit.Mixins;

/// <summary>
/// Audit-A2 mixin: the Metadata v2 graph factory functions that <see cref="WebAppFixture"/>
/// historically inlined as ~770 LOC of <c>BuildXxxTestGraph</c> / <c>GetSeededLayerXxx</c>
/// helpers. They are pure functions over layer ids and seed-file names — no fixture state —
/// so they belong on a stateless mixin that lives next to the fixture without bloating its
/// core surface. The mixin keeps the same default-graph shape (services + layers + bindings +
/// publications + style resources) the fixture needs to register into the test DI container.
/// </summary>
/// <remarks>
/// Per structural-audit-2026-05 (group A2), <c>WebAppFixture.cs</c> was 1,939 LOC and a
/// god-fixture for the Server test suite. Extracting the metadata-graph factories here is
/// the first concrete step in the mixin split; subsequent extractions should peel off
/// protocol-specific service-override / mock-setup blocks the same way. Behaviour is byte-
/// identical to the previous inline implementations — this is a pure relocation.
/// </remarks>
internal static class WebAppFixtureMetadataV2Mixin
{
    /// <summary>
    /// Default service id used by the test seed (mirrors <see cref="WebAppFixture.TestServiceId"/>).
    /// </summary>
    internal const string TestServiceId = "test";

    /// <summary>
    /// Default layer id used by the test seed (mirrors <see cref="WebAppFixture.TestLayerId"/>).
    /// </summary>
    internal const int TestLayerId = 0;

    private const string DefaultPointDrawingInfoJson = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSMS",
              "style": "esriSMSCircle",
              "color": [45, 105, 165, 255],
              "size": 6,
              "outline": {
                "type": "esriSLS",
                "style": "esriSLSSolid",
                "color": [45, 105, 165, 255],
                "width": 1
              }
            }
          }
        }
        """;

    /// <summary>
    /// Registers the default Metadata v2 graph provider into the test DI container.
    /// Removes the production <see cref="IMetadataV2GraphProvider"/> / <see cref="IMetadataV2GraphStore"/>
    /// registrations and replaces them with an in-memory <c>TestMetadataV2GraphProvider</c>
    /// seeded by <see cref="BuildDefaultTestGraph"/>.
    /// </summary>
    internal static void RegisterDefaultMetadataV2Graph(IServiceCollection services)
    {
        services.RemoveAll<IMetadataV2GraphProvider>();
        services.RemoveAll<IMetadataV2GraphStore>();
        services.AddSingleton(_ =>
            new Honua.TestKit.Infrastructure.TestMetadataV2GraphProvider(BuildDefaultTestGraph()));
        services.AddSingleton<IMetadataV2GraphProvider>(sp =>
            sp.GetRequiredService<Honua.TestKit.Infrastructure.TestMetadataV2GraphProvider>());
        services.AddSingleton<IMetadataV2GraphStore>(sp =>
            sp.GetRequiredService<Honua.TestKit.Infrastructure.TestMetadataV2GraphProvider>());
    }

    /// <summary>
    /// Builds the default Metadata v2 graph snapshot registered into the test DI container so
    /// that endpoints have a graph to read from. Mirrors the layer ids seeded by
    /// <c>tests/seed/server.yaml</c> so existence probes keyed on the layer id continue to find a
    /// matching publication. Tests that need a richer
    /// graph can replace the registration through <c>WebAppFixture.ConfigureServices</c>.
    /// </summary>
    internal static MetadataV2Graph BuildDefaultTestGraph()
    {
        // The Postgres test seed (tests/seed/server.yaml) registers a single "test" service
        // with every protocol enabled. Mirror that on the V2 graph so capability gates
        // (protocol enablement on the OGC API family) match the v1 behaviour.
        var allProtocols = MetadataV2ServiceProtocols.All;
        // tests/seed/server.yaml's "test" service is open by convention — anonymous
        // GETs against /rest/services/test/... must return data, matching the v1 default
        // posture. Set AllowAnonymous = true on the V2 service so the access middleware
        // doesn't 401 the anonymous fixture endpoints.
        var builder = new Honua.TestKit.Infrastructure.TestMetadataV2GraphBuilder()
            .AddService(
                "svc-test",
                "test",
                route: "/ogc/features",
                protocols: allProtocols,
                accessPolicy: new AccessPolicy { AllowAnonymous = true });

        // server.yaml binds only layers 0..2 to service "test"; do not publish helper
        // resource ids here or service-level FeatureServer queries will drift from the
        // v1 seed while the query handler cutover is still in progress.
        int[] defaultServiceLayerIndices = [0, 1, 2];

        // Cover resources inserted by the default seed plus the spatial-reference fixture
        // resources that BuildSpatialReferenceSeedTestGraph publishes under srid-test.
        int[] seededResourceLayerIndices = [0, 1, 2, 101, 102, 103];
        foreach (var layerIndex in seededResourceLayerIndices)
        {
            var resourceId = $"res-layer-{layerIndex}";
            var bindingId = $"binding-layer-{layerIndex}";
            builder
                .AddResource(
                    resourceId,
                    GetSeededLayerName(layerIndex),
                    MetadataV2ResourceType.FeatureDataset,
                    fields: GetSeededLayerSchemaFields(layerIndex),
                    spatial: GetSeededLayerSpatial(layerIndex),
                    temporal: GetSeededLayerTemporal(layerIndex))
                .AddStorageBinding(
                    bindingId,
                    resourceId,
                    "features",
                    storageLayerId: layerIndex,
                    // The seed's shared 'features' table uses 'geometry' as its
                    // geometry column; the schema field is named 'shape' (the V2
                    // logical name). FeatureStorageMapping reads geometryColumn
                    // from binding options to bridge the two, so add it here.
                    options: new Dictionary<string, JsonElement>
                    {
                        ["geometryColumn"] = JsonSerializer.SerializeToElement("geometry"),
                        ["attributesColumn"] = JsonSerializer.SerializeToElement("attributes")
                    });

            if (defaultServiceLayerIndices.Contains(layerIndex))
            {
                builder.AddPublication(
                    id: $"pub-layer-{layerIndex}",
                    serviceId: "svc-test",
                    resourceId: resourceId,
                    layerIndex: layerIndex,
                    serviceLocalId: layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // ImageServer publications for the canonical raster test layer (TestServiceId/TestLayerId).
        // ImageServer handler tests resolve their layer index against this snapshot.
        builder
            .AddResource("res-image-test", "test-layer", MetadataV2ResourceType.RasterDataset)
            .AddService("svc-image-test", TestServiceId, protocols: [MetadataV2ServiceProtocols.ImageServer])
            .AddPublication(
                id: "pub-image-test",
                serviceId: "svc-image-test",
                resourceId: "res-image-test",
                layerIndex: TestLayerId,
                serviceLocalId: "test-layer",
                publicationType: MetadataV2PublicationType.EsriImageLayer);

        // FeatureServer and MapServer publications for the canonical "test" service.
        // The GeoServices REST catalog endpoint enumerates these directly from the V2
        // graph, so the directory only emits FeatureServer/MapServer entries when matching
        // EsriFeatureService / EsriMapService publications exist. We publish the same
        // layer ids that server.yaml seeds so /rest/services has services to return and
        // downstream FeatureServer/MapServer handler ports can resolve them by layer id.
        builder
            .AddService("svc-test-feature", "test", protocols: [MetadataV2ServiceProtocols.FeatureServer])
            .AddService("svc-test-map", "test", protocols: [MetadataV2ServiceProtocols.MapServer])
            .AddService("svc-test-stac", "test", route: "/stac", protocols: [MetadataV2ServiceProtocols.Stac]);
        foreach (var layerIndex in defaultServiceLayerIndices)
        {
            var resourceId = $"res-layer-{layerIndex}";
            builder
                .AddPublication(
                    id: $"pub-feature-{layerIndex}",
                    serviceId: "svc-test-feature",
                    resourceId: resourceId,
                    layerIndex: layerIndex,
                    serviceLocalId: layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    publicationType: MetadataV2PublicationType.EsriFeatureLayer)
                .AddPublication(
                    id: $"pub-map-{layerIndex}",
                    serviceId: "svc-test-map",
                    resourceId: resourceId,
                    layerIndex: layerIndex,
                    serviceLocalId: layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    publicationType: MetadataV2PublicationType.EsriMapLayer)
                .AddPublication(
                    id: $"pub-stac-{layerIndex}",
                    serviceId: "svc-test-stac",
                    resourceId: resourceId,
                    layerIndex: layerIndex,
                    serviceLocalId: layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    publicationType: MetadataV2PublicationType.StacCollection);
        }

        var graph = builder.Build();
        var resources = graph.Resources.ToArray();
        var services = graph.Services.ToArray();
        for (var i = 0; i < services.Length; i++)
        {
            if (!string.Equals(services[i].Metadata.Name, TestServiceId, StringComparison.Ordinal))
            {
                continue;
            }

            services[i] = services[i] with
            {
                Metadata = services[i].Metadata with
                {
                    Description = "Test service for integration tests",
                },
            };
        }

        for (var i = 0; i < resources.Length; i++)
        {
            if (!string.Equals(resources[i].Metadata.Id, "res-layer-0", StringComparison.Ordinal))
            {
                continue;
            }

            resources[i] = resources[i] with
            {
                StyleResourceIds = resources[i].StyleResourceIds
                    .Append("style-layer-0-esri")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Relationships =
                [
                    new MetadataV2Relationship
                    {
                        Id = "1",
                        Name = "Test Relationship",
                        Description = "Test relationship between layer 0 and layer 1",
                        RelatedResourceId = "res-layer-1",
                        Role = "esriRelRoleOrigin",
                        OriginField = "objectid",
                        DestinationField = "related_id",
                        EsriRelationshipId = 1
                    },
                    new MetadataV2Relationship
                    {
                        Id = "2",
                        Name = "Secondary Relationship",
                        Description = "Secondary test relationship",
                        RelatedResourceId = "res-layer-2",
                        Role = "esriRelRoleOrigin",
                        OriginField = "objectid",
                        DestinationField = "secondary_id",
                        EsriRelationshipId = 2
                    },
                    // Regression (#1465 related-records): a relationship whose origin
                    // foreign key is NOT the object-id field. The related row's
                    // destination key therefore differs from the origin object id, so
                    // queryRelatedRecords must map related rows back to their origin
                    // object id via the stamped origin id rather than by the destination
                    // key value. Origin object id 1 carries ext_key='K-300208', and the
                    // layer-2 child references ext_key='K-300208'.
                    new MetadataV2Relationship
                    {
                        Id = "3",
                        Name = "External Key Relationship",
                        Description = "Relationship keyed on a non-objectid origin field",
                        RelatedResourceId = "res-layer-2",
                        Role = "esriRelRoleOrigin",
                        OriginField = "ext_key",
                        DestinationField = "ext_key",
                        EsriRelationshipId = 3
                    }
                ]
            };
        }

        resources = resources
            .Append(new MetadataV2Resource
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = "style-layer-0-esri",
                    Name = "Test Layer Drawing Info",
                    Description = "Default GeoServices drawingInfo for the seeded test layer",
                },
                Type = MetadataV2ResourceType.Style,
                Style = new MetadataV2ResourceStyle
                {
                    Title = "Test Layer Drawing Info",
                    Encodings =
                    [
                        new MetadataV2StyleEncoding
                        {
                            Encoding = "esri-drawing-info",
                            Body = DefaultPointDrawingInfoJson,
                            ContentType = "application/json",
                        },
                    ],
                },
            })
            .ToArray();

        return graph with { Resources = resources, Services = services };
    }

    internal static MetadataV2Graph BuildODataSeedTestGraph()
    {
        var allProtocols = MetadataV2ServiceProtocols.All;
        var builder = new Honua.TestKit.Infrastructure.TestMetadataV2GraphBuilder()
            .AddService(
                "svc-test",
                "test",
                route: "/odata",
                protocols: allProtocols);

        AddODataSeedLayer(
            builder,
            layerIndex: 0,
            name: "US Cities");
        AddODataSeedLayer(
            builder,
            layerIndex: 1,
            name: "City Landmarks");

        var graph = builder.Build();
        var resources = graph.Resources.ToArray();
        for (var i = 0; i < resources.Length; i++)
        {
            if (!string.Equals(resources[i].Metadata.Id, "res-layer-0", StringComparison.Ordinal))
            {
                continue;
            }

            resources[i] = resources[i] with
            {
                Relationships =
                [
                    new MetadataV2Relationship
                    {
                        Id = "1",
                        Name = "Landmarks",
                        Description = "City to landmark relationship",
                        RelatedResourceId = "res-layer-1",
                        Role = "esriRelRoleOrigin",
                        OriginField = "objectid",
                        DestinationField = "city_id",
                        EsriRelationshipId = 1
                    }
                ]
            };
        }

        return graph with { Resources = resources };
    }

    internal static MetadataV2Graph BuildSpatialReferenceSeedTestGraph()
    {
        var graph = BuildDefaultTestGraph();
        var services = graph.Services.ToList();
        var publications = graph.Publications.ToList();

        services.Add(new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "svc-srid-feature",
                Name = SpatialReferenceTestLayerCatalog.ServiceId
            },
            Protocols = [MetadataV2ServiceProtocols.FeatureServer]
        });
        services.Add(new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "svc-srid-odata",
                Name = SpatialReferenceTestLayerCatalog.ServiceId
            },
            Protocols = [MetadataV2ServiceProtocols.OData]
        });
        services.Add(new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "svc-srid-stac",
                Name = SpatialReferenceTestLayerCatalog.ServiceId
            },
            Route = "/stac",
            Protocols = [MetadataV2ServiceProtocols.Stac]
        });
        services.Add(new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "svc-srid-ogc-features",
                Name = SpatialReferenceTestLayerCatalog.ServiceId
            },
            Route = "/ogc/features",
            Protocols = [MetadataV2ServiceProtocols.OgcFeatures]
        });

        foreach (var layerIndex in new[]
                 {
                     SpatialReferenceTestLayerCatalog.PointLayerId,
                     SpatialReferenceTestLayerCatalog.LineLayerId,
                     SpatialReferenceTestLayerCatalog.PolygonLayerId
                 })
        {
            publications.Add(new MetadataV2Publication
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = $"pub-srid-feature-{layerIndex}",
                    Name = layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                ServiceId = "svc-srid-feature",
                ResourceId = $"res-layer-{layerIndex}",
                StorageBindingId = $"binding-layer-{layerIndex}",
                Identifier = new MetadataV2PublicationIdentifier
                {
                    Value = layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IsNumeric = true
                },
                PublicationType = MetadataV2PublicationType.EsriFeatureLayer
            });
            publications.Add(new MetadataV2Publication
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = $"pub-srid-odata-{layerIndex}",
                    Name = layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                ServiceId = "svc-srid-odata",
                ResourceId = $"res-layer-{layerIndex}",
                StorageBindingId = $"binding-layer-{layerIndex}",
                Identifier = new MetadataV2PublicationIdentifier
                {
                    Value = layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IsNumeric = true
                },
                PublicationType = MetadataV2PublicationType.ODataEntitySet
            });
            publications.Add(new MetadataV2Publication
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = $"pub-srid-stac-{layerIndex}",
                    Name = layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                ServiceId = "svc-srid-stac",
                ResourceId = $"res-layer-{layerIndex}",
                StorageBindingId = $"binding-layer-{layerIndex}",
                Identifier = new MetadataV2PublicationIdentifier
                {
                    Value = layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IsNumeric = true
                },
                PublicationType = MetadataV2PublicationType.StacCollection
            });
            publications.Add(new MetadataV2Publication
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = $"pub-srid-ogc-features-{layerIndex}",
                    Name = layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                ServiceId = "svc-srid-ogc-features",
                ResourceId = $"res-layer-{layerIndex}",
                StorageBindingId = $"binding-layer-{layerIndex}",
                Identifier = new MetadataV2PublicationIdentifier
                {
                    Value = layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IsNumeric = true
                },
                PublicationType = MetadataV2PublicationType.OgcCollection
            });
        }

        return graph with
        {
            Services = services,
            Publications = publications
        };
    }

    internal static MetadataV2Graph BuildAdminSampleMetadataV2Graph()
    {
        var builder = new Honua.TestKit.Infrastructure.TestMetadataV2GraphBuilder()
            .AddService(
                "svc-admin-sample-feature",
                "admin_sample",
                protocols: [MetadataV2ServiceProtocols.FeatureServer],
                accessPolicy: new AccessPolicy { AllowAnonymous = true });

        foreach (var layerIndex in new[] { 3000, 3001, 3002 })
        {
            var resourceId = $"res-admin-sample-{layerIndex}";
            var bindingId = $"binding-admin-sample-{layerIndex}";
            builder
                .AddResource(
                    resourceId,
                    GetAdminSampleLayerName(layerIndex),
                    MetadataV2ResourceType.FeatureDataset,
                    fields: GetAdminSampleSchemaFields(layerIndex),
                    accessPolicy: new AccessPolicy { AllowAnonymous = true },
                    spatial: GetAdminSampleSpatial(layerIndex))
                .AddStorageBinding(
                    bindingId,
                    resourceId,
                    "features",
                    storageLayerId: layerIndex)
                .AddPublication(
                    id: $"pub-admin-sample-feature-{layerIndex}",
                    serviceId: "svc-admin-sample-feature",
                    resourceId: resourceId,
                    layerIndex: layerIndex,
                    serviceLocalId: layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    publicationType: MetadataV2PublicationType.EsriFeatureLayer);
        }

        return builder.Build();
    }

    private static void AddODataSeedLayer(
        Honua.TestKit.Infrastructure.TestMetadataV2GraphBuilder builder,
        int layerIndex,
        string name)
    {
        var resourceId = $"res-layer-{layerIndex}";
        var bindingId = $"binding-layer-{layerIndex}";
        builder
            .AddResource(
                resourceId,
                name,
                MetadataV2ResourceType.FeatureDataset,
                fields: GetSeededLayerSchemaFields(layerIndex))
            .AddStorageBinding(
                bindingId,
                resourceId,
                "features",
                storageLayerId: layerIndex,
                options: new Dictionary<string, JsonElement>
                {
                    ["geometryColumn"] = JsonSerializer.SerializeToElement("geometry"),
                    ["attributesColumn"] = JsonSerializer.SerializeToElement("attributes")
                })
            .AddPublication(
                id: $"pub-layer-{layerIndex}",
                serviceId: "svc-test",
                resourceId: resourceId,
                layerIndex: layerIndex,
                storageBindingId: bindingId,
                serviceLocalId: layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                publicationType: MetadataV2PublicationType.ODataEntitySet);
    }

    /// <summary>
    /// Returns the V2 schema fields for the layers the Postgres test seeds
    /// (tests/seed/server.yaml, tests/seed/odata.yaml) populate via the v1
    /// layer_fields table. Returns the union of all known seed fields so the same
    /// fixture works for tests selecting different seed files. OGC API Features
    /// queryables, OData $metadata, STAC properties, and other schema-driven
    /// endpoints all read this from the V2 resource graph after the metadata-v2
    /// cutover. Layers without seeded fields (everything but 0/1/2) return an
    /// empty list — matching the v1 fixture behavior where those layers have no
    /// layer_fields rows.
    /// </summary>
    internal static IEnumerable<MetadataV2Field>? GetSeededLayerSchemaFields(int layerIndex)
        => layerIndex switch
        {
            0 =>
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Description = "Name" },
                new MetadataV2Field { Name = "description", Type = MetadataV2FieldType.String, Nullable = true, Description = "Description" },
                new MetadataV2Field { Name = "category", Type = MetadataV2FieldType.String, Nullable = true, Description = "Category" },
                new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.DateTime, Nullable = true, Description = "Timestamp" },
                new MetadataV2Field { Name = "event_date", Type = MetadataV2FieldType.DateTime, Nullable = true, Description = "Event date" },
                new MetadataV2Field { Name = "created_date", Type = MetadataV2FieldType.Date, Nullable = true, Description = "Created date" },
                new MetadataV2Field { Name = "population", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "City population" },
                new MetadataV2Field { Name = "area_sq_km", Type = MetadataV2FieldType.Double, Nullable = true, Description = "Area in square kilometers" },
                new MetadataV2Field { Name = "is_capital", Type = MetadataV2FieldType.Boolean, Nullable = true, Description = "Whether city is a state capital" },
                new MetadataV2Field { Name = "state", Type = MetadataV2FieldType.String, Nullable = true, Description = "State name" },
                new MetadataV2Field { Name = "country", Type = MetadataV2FieldType.String, Nullable = true, Description = "Country name" },
                new MetadataV2Field { Name = "founded_year", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "Year the city was founded" },
                new MetadataV2Field { Name = "rating", Type = MetadataV2FieldType.Double, Nullable = true, Description = "City rating" },
                new MetadataV2Field { Name = "notes", Type = MetadataV2FieldType.String, Nullable = true, Description = "Additional notes" },
                new MetadataV2Field { Name = "ext_key", Type = MetadataV2FieldType.String, Nullable = true, Description = "External relationship key (non-objectid origin field)" },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    Description = "Geometry",
                    SemanticRoles = ["geometry"],
                },
            ],
            1 =>
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Description = "Name field" },
                new MetadataV2Field { Name = "related_id", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "Foreign key to origin layer" },
                new MetadataV2Field { Name = "city_id", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "Origin city ID" },
                new MetadataV2Field { Name = "description", Type = MetadataV2FieldType.String, Nullable = true, Description = "Description" },
                new MetadataV2Field { Name = "category", Type = MetadataV2FieldType.String, Nullable = true, Description = "Landmark category" },
                new MetadataV2Field { Name = "established_year", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "Year established" },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    Description = "Geometry",
                    SemanticRoles = ["geometry"],
                },
            ],
            2 =>
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Description = "Name field" },
                new MetadataV2Field { Name = "secondary_id", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "Foreign key to origin layer" },
                new MetadataV2Field { Name = "ext_key", Type = MetadataV2FieldType.String, Nullable = true, Description = "External relationship key (matches a non-objectid origin field)" },
                new MetadataV2Field { Name = "type", Type = MetadataV2FieldType.String, Nullable = true, Description = "Type field" },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    Description = "Geometry",
                    SemanticRoles = ["geometry"],
                },
            ],
            SpatialReferenceTestLayerCatalog.PointLayerId or
            SpatialReferenceTestLayerCatalog.LineLayerId or
            SpatialReferenceTestLayerCatalog.PolygonLayerId =>
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Description = "Name" },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = false,
                    Description = "Geometry",
                    SemanticRoles = ["geometry"],
                },
            ],
            _ => null,
        };

    private static IEnumerable<MetadataV2Field> GetAdminSampleSchemaFields(int layerIndex)
        =>
        [
            new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
            new MetadataV2Field
            {
                Name = "name",
                Type = MetadataV2FieldType.String,
                Nullable = false,
                Description = layerIndex switch
                {
                    3000 => "Site or asset name",
                    3001 => "Route name",
                    3002 => "Area name",
                    _ => "Name",
                },
            },
            new MetadataV2Field
            {
                Name = "category",
                Type = MetadataV2FieldType.String,
                Nullable = false,
                Description = layerIndex switch
                {
                    3000 => "Operational category",
                    3001 => "Route category",
                    3002 => "Area category",
                    _ => "Category",
                },
            },
            new MetadataV2Field
            {
                Name = "status",
                Type = MetadataV2FieldType.String,
                Nullable = false,
                Description = layerIndex switch
                {
                    3000 => "Current operating status",
                    3001 => "Current route status",
                    3002 => "Current area status",
                    _ => "Current status",
                },
            },
            new MetadataV2Field { Name = "priority", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "Operator triage priority" },
            new MetadataV2Field { Name = "owner", Type = MetadataV2FieldType.String, Nullable = true, Description = "Responsible team" },
            new MetadataV2Field { Name = "updated_at", Type = MetadataV2FieldType.DateTime, Nullable = true, Description = "Last sample update timestamp" },
            new MetadataV2Field
            {
                Name = "shape",
                Type = MetadataV2FieldType.Geometry,
                Nullable = true,
                Description = "Geometry",
                SemanticRoles = ["geometry"],
            },
        ];

    private static string GetAdminSampleLayerName(int layerIndex)
        => layerIndex switch
        {
            3000 => "Oahu Operations Sites",
            3001 => "Oahu Response Routes",
            3002 => "Oahu Service Areas",
            _ => $"admin-sample-{layerIndex}",
        };

    private static MetadataV2ResourceSpatial GetAdminSampleSpatial(int layerIndex)
        => layerIndex switch
        {
            3000 => new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                Bbox = new MetadataV2Bbox
                {
                    West = -158.0011,
                    South = 21.3069,
                    East = -157.7394,
                    North = 21.3972,
                },
                SupportedCrs = [MetadataV2SpatialReference.Wgs84],
            },
            3001 => new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.WebMercator,
                StorageCrs = MetadataV2SpatialReference.WebMercator,
                GeometryType = MetadataV2GeometryType.LineString,
                PrimaryGeometryField = "shape",
                Bbox = new MetadataV2Bbox
                {
                    West = -158.0011,
                    South = 21.3069,
                    East = -157.7394,
                    North = 21.3972,
                },
                SupportedCrs = [MetadataV2SpatialReference.WebMercator, MetadataV2SpatialReference.Wgs84],
            },
            3002 => new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Polygon,
                PrimaryGeometryField = "shape",
                Bbox = new MetadataV2Bbox
                {
                    West = -157.95,
                    South = 21.29,
                    East = -157.70,
                    North = 21.43,
                },
                SupportedCrs = [MetadataV2SpatialReference.Wgs84],
            },
            _ => new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                SupportedCrs = [MetadataV2SpatialReference.Wgs84],
            },
        };

    private static string GetSeededLayerName(int layerIndex)
        => layerIndex switch
        {
            0 => "Test Layer",
            1 => "Related Test Layer 1",
            2 => "Secondary Related Layer",
            SpatialReferenceTestLayerCatalog.PointLayerId => "SRID Test Points",
            SpatialReferenceTestLayerCatalog.LineLayerId => "SRID Test Lines",
            SpatialReferenceTestLayerCatalog.PolygonLayerId => "SRID Test Polygons",
            _ => $"layer-{layerIndex}",
        };

    private static MetadataV2ResourceSpatial? GetSeededLayerSpatial(int layerIndex)
        => layerIndex switch
        {
            0 => new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                Bbox = new MetadataV2Bbox
                {
                    West = -123,
                    South = 37,
                    East = -122,
                    North = 38,
                },
                SupportedCrs = [MetadataV2SpatialReference.Wgs84],
            },
            1 or 2 => new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                SupportedCrs = [MetadataV2SpatialReference.Wgs84],
            },
            SpatialReferenceTestLayerCatalog.PointLayerId => new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.WebMercator,
                StorageCrs = MetadataV2SpatialReference.WebMercator,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                SupportedCrs = [MetadataV2SpatialReference.WebMercator, MetadataV2SpatialReference.Wgs84],
            },
            SpatialReferenceTestLayerCatalog.LineLayerId => new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.WebMercator,
                StorageCrs = MetadataV2SpatialReference.WebMercator,
                GeometryType = MetadataV2GeometryType.LineString,
                PrimaryGeometryField = "shape",
                SupportedCrs = [MetadataV2SpatialReference.WebMercator, MetadataV2SpatialReference.Wgs84],
            },
            SpatialReferenceTestLayerCatalog.PolygonLayerId => new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.WebMercator,
                StorageCrs = MetadataV2SpatialReference.WebMercator,
                GeometryType = MetadataV2GeometryType.Polygon,
                PrimaryGeometryField = "shape",
                SupportedCrs = [MetadataV2SpatialReference.WebMercator, MetadataV2SpatialReference.Wgs84],
            },
            _ => null,
        };

    private static MetadataV2ResourceTemporal? GetSeededLayerTemporal(int layerIndex)
        => layerIndex == 0
            ? new MetadataV2ResourceTemporal
            {
                StartTimeField = "timestamp",
                EndTimeField = "event_date",
            }
            : null;
}
