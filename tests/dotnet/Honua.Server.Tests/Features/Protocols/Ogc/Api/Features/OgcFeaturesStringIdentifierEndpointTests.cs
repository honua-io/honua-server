// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class OgcFeaturesStringIdentifierEndpointTests
{
    private const int StringIdLayerId = 99;

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithStringIds_ReturnsMatchingFeature()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/ogc/features/collections/{StringIdLayerId}/items?ids=alpha-1");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().ContainSingle();
        features[0].GetProperty("id").GetString().Should().Be("alpha-1");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithStringCollectionId_ReturnsMatchingFeature()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/ogc/features/collections/String%20ID%20Layer/items?ids=alpha-1");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().ContainSingle();
        features[0].GetProperty("id").GetString().Should().Be("alpha-1");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithNumericLookingStringCollectionId_ResolvesCollectionName()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/ogc/features/collections/123/items?ids=alpha-1");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().ContainSingle();
        features[0].GetProperty("properties").GetProperty("name").GetString().Should().Be("Numeric Collection Name Alpha");
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetItem_WithStringFeatureId_ReturnsFeature()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/ogc/features/collections/{StringIdLayerId}/items/alpha-1");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("id").GetString().Should().Be("alpha-1");
        document.RootElement.GetProperty("properties").GetProperty("name").GetString().Should().Be("String ID Alpha");
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetItem_WithNumericStringFeatureId_UsesPublicIdentifierBeforeObjectId()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/ogc/features/collections/{StringIdLayerId}/items/1");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("id").GetString().Should().Be("1");
        document.RootElement.GetProperty("properties").GetProperty("name").GetString().Should().Be("String ID Numeric Collision");
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetItem_WithNumericObjectIdButNoPublicIdentifier_ReturnsNotFound()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/ogc/features/collections/{StringIdLayerId}/items/2");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetItem_WithLeadingZeroStringFeatureId_PreservesIdentifier()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/ogc/features/collections/{StringIdLayerId}/items/001");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("id").GetString().Should().Be("001");
        document.RootElement.GetProperty("properties").GetProperty("name").GetString().Should().Be("String ID Leading Zero");
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetItem_WithSpaceInStringFeatureId_EscapesLinks()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/ogc/features/collections/{StringIdLayerId}/items/space%20id");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("id").GetString().Should().Be("space id");

        var links = document.RootElement.GetProperty("links").EnumerateArray().ToArray();
        var selfHref = links.Single(link => link.GetProperty("rel").GetString() == "self").GetProperty("href").GetString();
        selfHref.Should().Contain("/items/space%20id");
        selfHref.Should().NotContain("/items/space id");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PutItem_WithStringFeatureId_UsesPublicIdentifier()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = "alpha-1",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.1, 37.1]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["id"] = "alpha-1",
                ["name"] = "String ID Alpha Updated"
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(feature), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.GeoJson);

        var response = await client.PutAsync(
            $"/ogc/features/collections/{StringIdLayerId}/items/alpha-1",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("id").GetString().Should().Be("alpha-1");
        document.RootElement.GetProperty("properties").GetProperty("name").GetString().Should().Be("String ID Alpha Updated");
    }

    [IntegrationTest]
    [Operation(Operations.Create, Operations.GetById)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task CreateItem_WithTopLevelStringFeatureId_StoresPublicIdentifier()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string publicId = "top-level-created";
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = publicId,
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.7, 37.7]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "Top Level ID Created"
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(feature), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.GeoJson);

        var response = await client.PostAsync(
            $"/ogc/features/collections/{StringIdLayerId}/items",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        createDocument.RootElement.GetProperty("id").GetString().Should().Be(publicId);
        createDocument.RootElement.GetProperty("properties").GetProperty("id").GetString().Should().Be(publicId);

        var getResponse = await client.GetAsync(
            $"/ogc/features/collections/{StringIdLayerId}/items/{publicId}");

        getResponse.Be200Ok();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateItem_WithConflictingTopLevelAndPropertyIds_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = "top-level-id",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.7, 37.7]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["id"] = "property-id",
                ["name"] = "Conflicting IDs"
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(feature), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.GeoJson);

        var response = await client.PostAsync(
            $"/ogc/features/collections/{StringIdLayerId}/items",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PutItem_WithTopLevelOnlyStringFeatureId_PreservesPublicIdentifier()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = "alpha-1",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.1, 37.1]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "Top Level Only Updated"
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(feature), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.GeoJson);

        var response = await client.PutAsync(
            $"/ogc/features/collections/{StringIdLayerId}/items/alpha-1",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("id").GetString().Should().Be("alpha-1");
        document.RootElement.GetProperty("properties").GetProperty("id").GetString().Should().Be("alpha-1");
        document.RootElement.GetProperty("properties").GetProperty("name").GetString().Should().Be("Top Level Only Updated");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PutItem_WithInternalObjectIdPayloadId_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = 1,
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.1, 37.1]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["id"] = "alpha-1",
                ["name"] = "Should Not Update"
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(feature), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.GeoJson);

        var response = await client.PutAsync(
            $"/ogc/features/collections/{StringIdLayerId}/items/alpha-1",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PutItem_WithMismatchedPublicIdProperty_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = "alpha-1",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.1, 37.1]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["id"] = "beta-2",
                ["name"] = "Should Not Update"
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(feature), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.GeoJson);

        var response = await client.PutAsync(
            $"/ogc/features/collections/{StringIdLayerId}/items/alpha-1",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PatchItem_WithMismatchedPublicIdProperty_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/ogc/features/collections/{StringIdLayerId}/items/alpha-1")
        {
            Content = new StringContent(
                """
                {
                  "properties": {
                    "id": "beta-2",
                    "name": "Should Not Update"
                  }
                }
                """,
                Encoding.UTF8,
                "application/merge-patch+json")
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.BulkUpdate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task BatchUpdate_WithMismatchedPayloadPublicId_ReturnsMultiStatus()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var batchRequest = new BatchRequest
        {
            Operations =
            [
                new BatchOperation
                {
                    Id = "update-mismatched-id",
                    Type = "UPDATE",
                    FeatureId = "alpha-1",
                    Feature = new GeoJsonFeature
                    {
                        Type = "Feature",
                        Id = "beta-2",
                        Geometry = new SimpleGeoJsonGeometry
                        {
                            Type = "Point",
                            CoordinatesJson = "[-122.6, 37.6]"
                        },
                        Properties = new Dictionary<string, object?>
                        {
                            ["id"] = "beta-2",
                            ["name"] = "Should Not Update"
                        }
                    }
                }
            ]
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(batchRequest, OgcJsonContext.Default.BatchRequest),
            Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Json);

        var response = await client.PostAsync(
            $"/ogc/features/collections/{StringIdLayerId}/items/batch",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var responseContent = await response.Content.ReadAsStringAsync();
        var batchResponse = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.BatchOperationResponse);
        batchResponse.Should().NotBeNull();
        batchResponse!.HasErrors.Should().BeTrue();
        batchResponse.Results.Should().ContainSingle(result =>
            result.OperationId == "update-mismatched-id" &&
            !result.IsSuccess &&
            result.StatusCode == 400);
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate, Operations.GetById)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task BatchCreate_WithStringFeatureId_ReturnsPublicIdentifierUsableForGet()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string publicId = "created-public-1";
        var batchRequest = new BatchRequest
        {
            Operations =
            [
                new BatchOperation
                {
                    Id = "create-string-id",
                    Type = "CREATE",
                    Feature = new GeoJsonFeature
                    {
                        Type = "Feature",
                        Id = publicId,
                        Geometry = new SimpleGeoJsonGeometry
                        {
                            Type = "Point",
                            CoordinatesJson = "[-122.6, 37.6]"
                        },
                        Properties = new Dictionary<string, object?>
                        {
                            ["id"] = publicId,
                            ["name"] = "String ID Batch Created"
                        }
                    }
                }
            ]
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(batchRequest, OgcJsonContext.Default.BatchRequest),
            Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Json);

        var response = await client.PostAsync(
            $"/ogc/features/collections/{StringIdLayerId}/items/batch",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseContent = await response.Content.ReadAsStringAsync();
        var batchResponse = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.BatchOperationResponse);
        batchResponse.Should().NotBeNull();
        batchResponse!.HasErrors.Should().BeFalse();
        batchResponse.Results.Should().ContainSingle();

        var result = batchResponse.Results.Single();
        result.OperationId.Should().Be("create-string-id");
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(201);
        result.FeatureId.Should().Be(publicId);

        var getResponse = await client.GetAsync(
            $"/ogc/features/collections/{StringIdLayerId}/items/{Uri.EscapeDataString(result.FeatureId!)}");

        getResponse.Be200Ok();
        using var document = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("id").GetString().Should().Be(publicId);
        document.RootElement.GetProperty("properties").GetProperty("name").GetString().Should().Be("String ID Batch Created");
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TestFeatureStore>();
                services.RemoveAll<IFeatureReader>();
                services.RemoveAll<IFeatureWriter>();
                services.RemoveAll<ITileProvider>();
                services.RemoveAll<IRelationshipStore>();
                services.RemoveAll<IStreamingFeatureStore>();
                services.RemoveAll<ILayerCatalog>();
                services.RemoveAll<ICrsRegistry>();
                services.RemoveAll<ICoordinateTransformService>();
                services.RemoveAll<IGeometryTopologyValidator>();
                services.RemoveAll<ISqlFilterTranslator>();
                services.AddSingleton<TestFeatureStore>();
                services.AddSingleton<IFeatureReader>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddSingleton<IFeatureWriter>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddSingleton<ITileProvider>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddSingleton<IRelationshipStore>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddSingleton<IStreamingFeatureStore>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddScoped<ILayerCatalog, StringIdLayerCatalog>();
                services.AddSingleton<ICrsRegistry, StringIdCrsRegistry>();
                services.AddSingleton<ICoordinateTransformService, IdentityCoordinateTransformService>();
                services.AddSingleton<IGeometryTopologyValidator, NoOpGeometryTopologyValidator>();
                services.AddScoped<ISqlFilterTranslator, StringIdSqlFilterTranslator>();

                // V2-ported OGC API Features endpoints resolve collections through
                // IMetadataV2GraphProvider; mirror the v1 StringIdLayerCatalog content
                // on the V2 graph so the V2 ValidateCollectionWithAccessV2Async + storage
                // layer id resolution match the layers this fixture exposes.
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.AddSingleton<IMetadataV2GraphProvider>(_ => BuildStringIdGraphProvider());
            });
        });
    }

    private sealed class StringIdSqlFilterTranslator : ISqlFilterTranslator
    {
        public SqlFragment Translate(FilterExpression filter, LayerDefinition layer)
            => TranslateCore(filter);

        public SqlFragment Translate(FilterExpression filter, MetadataV2Resource resource)
            => TranslateCore(filter);

        private static SqlFragment TranslateCore(FilterExpression filter)
        {
            if (filter is BinaryExpression
                {
                    Left: PropertyReference { PropertyName: "id" },
                    Operator: BinaryOperator.In,
                    Right: ValueList valueList
                })
            {
                var values = valueList.Values
                    .OfType<Literal>()
                    .Select(static literal => literal.Value)
                    .ToArray();
                var parameterNames = values
                    .Select(static (_, index) => $"@p{index}")
                    .ToArray();

                return new SqlFragment(
                    $"id IN ({string.Join(", ", parameterNames)})",
                    values);
            }

            return new SqlFragment("1=1", Array.Empty<object?>());
        }
    }

    private sealed class IdentityCoordinateTransformService : ICoordinateTransformService
    {
        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX,
            double minY,
            double maxX,
            double maxY,
            int fromSrid,
            int toSrid,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<(double MinX, double MinY, double MaxX, double MaxY)?>((minX, minY, maxX, maxY));

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x,
            double y,
            int fromSrid,
            int toSrid,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<(double X, double Y)?>((x, y));
    }

    private sealed class NoOpGeometryTopologyValidator : IGeometryTopologyValidator
    {
        public Task<GeometryValidationResult> ValidateTopologyAsync(byte[] wkb, CancellationToken cancellationToken = default)
            => Task.FromResult(GeometryValidationResult.Success());

        public Task<GeometryRepairResult> RepairAsync(byte[] wkb, CancellationToken cancellationToken = default)
            => Task.FromResult(GeometryRepairResult.NotNeeded(wkb));
    }

    private sealed class StringIdCrsRegistry : ICrsRegistry
    {
        private static readonly CrsDefinition Crs84 = new(
            "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
            4326,
            AxisOrder.EastNorth,
            true);

        private static readonly CrsDefinition Epsg4326 = new(
            "http://www.opengis.net/def/crs/EPSG/0/4326",
            4326,
            AxisOrder.NorthEast,
            true);

        public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(crsIdentifier) ||
                crsIdentifier.Equals(Crs84.Uri, StringComparison.OrdinalIgnoreCase) ||
                crsIdentifier.Equals("CRS84", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult<CrsDefinition?>(Crs84);
            }

            if (crsIdentifier.Equals(Epsg4326.Uri, StringComparison.OrdinalIgnoreCase) ||
                crsIdentifier.Equals("EPSG:4326", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult<CrsDefinition?>(Epsg4326);
            }

            return ValueTask.FromResult<CrsDefinition?>(null);
        }

        public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(srid == 4326 ? Epsg4326 : null);

        public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(srid == 4326);
    }

    private sealed class StringIdLayerCatalog : ILayerCatalog
    {
        private static readonly SpatialReference SpatialReference = SpatialReference.Create(4326);
        private static readonly FeatureExtent Extent = FeatureExtent.Create(-180, -90, 180, 90, 4326);
        private static readonly CatalogMetadata Metadata = new()
        {
            AccessPolicy = new AccessPolicy
            {
                AllowAnonymous = true,
                AllowAnonymousWrite = true
            }
        };
        private static readonly LayerDefinition Layer = new(
            Id: StringIdLayerId,
            Name: "String ID Layer",
            Description: "Layer with string-valued public feature identifiers.",
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference,
            Fields:
            [
                new FieldDefinition("id", FieldType.String, Length: 64, Nullable: false),
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 255)
            ],
            Extent: Extent,
            MinScale: null,
            MaxScale: null,
            DefaultVisibility: true,
            Metadata: Metadata);
        private static readonly LayerDefinition NumericNameLayer = Layer with
        {
            Id = 100,
            Name = "123",
            Description = "Layer with a numeric-looking collection name."
        };
        private static readonly ServiceDefinition Service = new(
            "string-ids",
            "Feature service for string identifier tests",
            [Layer, NumericNameLayer],
            SpatialReference,
            Metadata: Metadata);

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId switch
            {
                StringIdLayerId => Layer,
                100 => NumericNameLayer,
                _ => null
            });

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { Layer, NumericNameLayer });

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, Service.Name, StringComparison.OrdinalIgnoreCase) ? Service : null);

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { Service });

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId is StringIdLayerId or 100);

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, Service.Name, StringComparison.OrdinalIgnoreCase));

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }

    private static TestMetadataV2GraphProvider BuildStringIdGraphProvider()
    {
        // Mirror the v1 StringIdLayerCatalog content (StringIdLayerId=99 + 100).
        // Both layers carry a string-typed public id field plus an integer object id
        // so the V2 OGC API Features routes can resolve string/feature-id lookups.
        var openAccessPolicy = new Honua.Core.Features.Security.Domain.AccessPolicy
        {
            AllowAnonymous = true,
            AllowAnonymousWrite = true,
        };
        MetadataV2Field[] fields =
        [
            new()
            {
                Name = "id",
                Type = MetadataV2FieldType.String,
                Nullable = false,
                Length = 64,
                SemanticRoles = new[] { "id.primary" },
            },
            new() { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
            new() { Name = "name", Type = MetadataV2FieldType.String, Length = 255 },
        ];

        return new TestMetadataV2GraphBuilder()
            .AddService(
                "svc-string-ids",
                "string-ids",
                route: "/ogc/features",
                protocols: new[] { ServiceProtocols.OgcFeatures },
                accessPolicy: openAccessPolicy)
            .AddResource(
                "res-string-id-layer",
                "String ID Layer",
                MetadataV2ResourceType.FeatureDataset,
                fields: fields,
                accessPolicy: openAccessPolicy)
            .AddResource(
                "res-numeric-name-layer",
                "123",
                MetadataV2ResourceType.FeatureDataset,
                fields: fields,
                accessPolicy: openAccessPolicy)
            .AddPublication(
                id: "pub-string-id-layer",
                serviceId: "svc-string-ids",
                resourceId: "res-string-id-layer",
                layerIndex: StringIdLayerId,
                path: "String ID Layer",
                serviceLocalId: StringIdLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AddPublication(
                id: "pub-numeric-name-layer",
                serviceId: "svc-string-ids",
                resourceId: "res-numeric-name-layer",
                layerIndex: 100,
                serviceLocalId: "123")
            .BuildProvider();
    }
}
