// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

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
                services.RemoveAll<ICrsDetectionService>();
                services.RemoveAll<ICrsRegistry>();
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton<ICrsDetectionService, NoopCrsDetectionService>();
                services.AddSingleton<ICrsRegistry, StringIdCrsRegistry>();
                services.AddSingleton(_ => BuildStringIdGraphProvider());
                services.AddSingleton<IMetadataV2GraphProvider>(provider =>
                    provider.GetRequiredService<TestMetadataV2GraphProvider>());
                services.AddSingleton<IMetadataV2GraphStore>(provider =>
                    provider.GetRequiredService<TestMetadataV2GraphProvider>());
            });
        });

    private static TestMetadataV2GraphProvider BuildStringIdGraphProvider()
    {
        var openAccessPolicy = new Honua.Core.Features.Security.Domain.AccessPolicy
        {
            AllowAnonymous = true,
            AllowAnonymousWrite = true
        };

        MetadataV2Field[] fields =
        [
            new()
            {
                Name = "id",
                Type = MetadataV2FieldType.String,
                Nullable = false,
                Length = 64,
                SemanticRoles = ["id.primary"]
            },
            new() { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new() { Name = "name", Type = MetadataV2FieldType.String, Length = 255 },
            new()
            {
                Name = "shape",
                Type = MetadataV2FieldType.Geometry,
                Nullable = true,
                SemanticRoles = ["geometry"]
            }
        ];

        return new TestMetadataV2GraphBuilder()
            .AddService(
                "svc-string-ids",
                ServiceName,
                route: $"/rest/services/{ServiceName}/MapServer",
                protocols: [MetadataV2ServiceProtocols.MapServer],
                accessPolicy: openAccessPolicy)
            .AddResource(
                "res-string-id-layer",
                "String ID Layer",
                MetadataV2ResourceType.FeatureDataset,
                fields: fields,
                accessPolicy: openAccessPolicy)
            .AddStorageBinding(
                "binding-string-id-layer",
                "res-string-id-layer",
                $"test.layers.{StringIdLayerId}",
                storageLayerId: StringIdLayerId)
            .AddPublication(
                id: "pub-string-id-layer",
                serviceId: "svc-string-ids",
                resourceId: "res-string-id-layer",
                layerIndex: StringIdLayerId,
                storageBindingId: "binding-string-id-layer",
                serviceLocalId: StringIdLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                publicationType: MetadataV2PublicationType.EsriMapLayer)
            .BuildProvider();
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
}
