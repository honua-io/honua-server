// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Protocol(TestProtocols.MapServer)]
public sealed class MapServerObjectIdFieldTests
{
    private const int StringIdLayerId = 99;
    private const string ServiceName = "string-ids";

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task LayerMetadata_WithStringIdAndNumericObjectId_UsesNumericObjectIdField()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/rest/services/{ServiceName}/MapServer/{StringIdLayerId}?f=json");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var layer = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.MapServerLayerResponse);

        layer.Should().NotBeNull();
        layer!.ObjectIdField.Should().Be(FieldNames.ObjectId);
        layer.Fields.Should().Contain(field => field.Name == "id" && field.Type == "esriFieldTypeString");
        layer.Fields.Should().Contain(field => field.Name == FieldNames.ObjectId && field.Type == "esriFieldTypeInteger");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task Query_WithStringIdAndNumericObjectId_UsesNumericObjectIdAndPreservesStringId()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/{StringIdLayerId}/query?f=json&returnGeometry=false&resultRecordCount=1");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var query = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        query.Should().NotBeNull();
        query!.ObjectIdFieldName.Should().Be(FieldNames.ObjectId);
        query.Fields.Should().Contain(field => field.Name == "id" && field.Type == "esriFieldTypeString");
        query.Fields.Should().Contain(field => field.Name == FieldNames.ObjectId && field.Type == "esriFieldTypeInteger");

        var attributes = query.Features.Should().ContainSingle().Subject.Attributes;
        ReadStringAttribute(attributes, "id").Should().Be("alpha-1");
        ReadInt64Attribute(attributes, FieldNames.ObjectId).Should().Be(1);
    }

    private static string? ReadStringAttribute(Dictionary<string, object?> attributes, string key)
    {
        attributes.TryGetValue(key, out var value).Should().BeTrue();
        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => value?.ToString()
        };
    }

    private static long? ReadInt64Attribute(Dictionary<string, object?> attributes, string key)
    {
        attributes.TryGetValue(key, out var value).Should().BeTrue();
        return value switch
        {
            long number => number,
            int number => number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number) => number,
            _ => null
        };
    }

    private static WebApplicationFactory<Program> CreateFactory()
        => new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILayerCatalog>();
                services.RemoveAll<ICrsDetectionService>();
                services.RemoveAll<ICrsRegistry>();
                services.AddScoped<ILayerCatalog, StringIdLayerCatalog>();
                services.AddSingleton<ICrsDetectionService, NoopCrsDetectionService>();
                services.AddSingleton<ICrsRegistry, StringIdCrsRegistry>();
            });
        });

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
                new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 255)
            ],
            Extent: Extent,
            MinScale: null,
            MaxScale: null,
            DefaultVisibility: true,
            Metadata: Metadata);

        private static readonly ServiceDefinition Service = new(
            ServiceName,
            "Feature service for string identifier tests",
            [Layer],
            SpatialReference,
            Metadata: Metadata);

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId == StringIdLayerId ? Layer : null);

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { Layer });

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, Service.Name, StringComparison.OrdinalIgnoreCase) ? Service : null);

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { Service });

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId == StringIdLayerId);

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, Service.Name, StringComparison.OrdinalIgnoreCase));

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }
}
