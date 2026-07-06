// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.MapServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>
/// Integration coverage for MapServer <c>dynamicLayers</c> <c>joinTable</c> data sources (#1866).
/// A joinTable joins an allowlisted left (geometry/identity) layer to an allowlisted right
/// (attribute) layer on a key; joined attributes are surfaced through identify and the dynamicLayer
/// metadata resource. The join honours <c>joinType</c> (left outer / inner), enforces the right
/// layer's access policy, and is parameter-safe (no raw SQL).
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerDynamicJoinTests
{
    private const string ServiceName = "join-map";
    private const string WorkspaceId = "analytics-workspace";

    // Left layer maps to TestFeatureStore layer 0 (5 features, objectid 1..5 + "category").
    private const int LeftLayerId = 0;
    private const int LeftStorageId = 0;

    // Right layer maps to TestFeatureStore layer 99 (5 features, objectid 1..5 + string "id"/"name").
    private const int RightLayerId = 1;
    private const int RightStorageId = 99;

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task Identify_WithLeftOuterJoin_ReturnsQualifiedJoinedAttributes()
    {
        using var factory = CreateFactory(rightLayerAnonymous: true);
        using var client = factory.CreateClient();

        var dynamicLayers = Uri.EscapeDataString(BuildJoinDynamicLayers("esriLeftOuterJoin"));
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/identify" +
            "?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90" +
            "&imageDisplay=800,600,96&tolerance=10&layers=all" +
            $"&dynamicLayers={dynamicLayers}&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        var results = document.RootElement.GetProperty("results");
        results.GetArrayLength().Should().BeGreaterThan(0, content);

        var first = results[0].GetProperty("attributes");
        // Left attribute retained.
        first.TryGetProperty("category", out _).Should().BeTrue(content);
        // Right attribute qualified by the right table name.
        first.TryGetProperty("right_parcels.name", out var joinedName).Should().BeTrue(content);
        joinedName.GetString().Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task Identify_WithInnerJoinAndNoMatch_DropsUnmatchedLeftFeatures()
    {
        using var factory = CreateFactory(rightLayerAnonymous: true);
        using var client = factory.CreateClient();

        // Join on a key that never matches (left objectid vs right "id" string values like "alpha-1").
        var dynamicLayers = Uri.EscapeDataString(BuildJoinDynamicLayers(
            "esriLeftInnerJoin",
            leftKey: "objectid",
            rightKey: "id"));
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/identify" +
            "?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90" +
            "&imageDisplay=800,600,96&tolerance=10&layers=all" +
            $"&dynamicLayers={dynamicLayers}&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("results").GetArrayLength().Should().Be(0, content);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task Identify_WithInaccessibleRightLayer_ReturnsBadRequest()
    {
        // Right layer denies anonymous access: the join must be rejected rather than leaking
        // the right layer's attributes.
        using var factory = CreateFactory(rightLayerAnonymous: false);
        using var client = factory.CreateClient();

        var dynamicLayers = Uri.EscapeDataString(BuildJoinDynamicLayers("esriLeftOuterJoin"));
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/identify" +
            "?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90" +
            "&imageDisplay=800,600,96&tolerance=10&layers=all" +
            $"&dynamicLayers={dynamicLayers}&f=json");

        var content = await response.Content.ReadAsStringAsync();
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("inaccessible right layer");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task DynamicLayer_WithJoinTable_ReportsQualifiedJoinedFields()
    {
        using var factory = CreateFactory(rightLayerAnonymous: true);
        using var client = factory.CreateClient();

        var layer = Uri.EscapeDataString(BuildJoinLayer("esriLeftOuterJoin"));
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/dynamicLayer?f=json&layer={layer}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        var fields = document.RootElement.GetProperty("fields");
        var fieldNames = fields.EnumerateArray()
            .Select(f => f.GetProperty("name").GetString())
            .ToArray();

        // Left field present unqualified, right field present qualified.
        fieldNames.Should().Contain("category");
        fieldNames.Should().Contain("right_parcels.name");
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task Identify_WithJoinKeyContainingInjection_ReturnsBadRequest()
    {
        // A join key is restricted to a plain field identifier; a key that is not a bare identifier
        // (here, a qualified name with a dot) must be rejected at validation, never interpolated
        // into a query.
        using var factory = CreateFactory(rightLayerAnonymous: true);
        using var client = factory.CreateClient();

        var dynamicLayers = Uri.EscapeDataString(BuildJoinDynamicLayers(
            "esriLeftOuterJoin",
            leftKey: "objectid",
            rightKey: "parcels.objectid"));
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/identify" +
            "?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90" +
            "&imageDisplay=800,600,96&tolerance=10&layers=all" +
            $"&dynamicLayers={dynamicLayers}&f=json");

        var content = await response.Content.ReadAsStringAsync();
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("not a valid field name");
    }

    private static string BuildJoinDynamicLayers(
        string joinType,
        string leftKey = "objectid",
        string rightKey = "objectid")
        => "[" + BuildJoinLayer(joinType, leftKey, rightKey) + "]";

    private static string BuildJoinLayer(
        string joinType,
        string leftKey = "objectid",
        string rightKey = "objectid")
        => "{\"id\":42,\"source\":{\"type\":\"dataLayer\",\"dataSource\":{\"type\":\"joinTable\","
            + $"\"leftTableSource\":{{\"type\":\"mapLayer\",\"mapLayerId\":{LeftLayerId}}},"
            + $"\"rightTableSource\":{{\"type\":\"mapLayer\",\"mapLayerId\":{RightLayerId}}},"
            + $"\"leftTableKey\":\"{leftKey}\",\"rightTableKey\":\"{rightKey}\","
            + $"\"joinType\":\"{joinType}\"}}}}}}";

    private static WebApplicationFactory<Program> CreateFactory(bool rightLayerAnonymous)
        => new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton(_ => BuildJoinGraphProvider(rightLayerAnonymous));
                services.AddSingleton<IMetadataV2GraphProvider>(provider =>
                    provider.GetRequiredService<TestMetadataV2GraphProvider>());
                services.AddSingleton<IMetadataV2GraphStore>(provider =>
                    provider.GetRequiredService<TestMetadataV2GraphProvider>());

                services.Configure<MapServerDynamicLayersOptions>(options =>
                {
                    options.WorkspaceLayersEnabled = true;
                    options.AllowAllRegisteredWorkspaces = false;
                    options.AllowedWorkspaceIds.Clear();
                    options.AllowedWorkspaceIds.Add(WorkspaceId);
                });
            });
        });

    private static TestMetadataV2GraphProvider BuildJoinGraphProvider(bool rightLayerAnonymous)
    {
        var openAccessPolicy = new AccessPolicy { AllowAnonymous = true, AllowAnonymousWrite = true };
        var rightAccessPolicy = rightLayerAnonymous
            ? openAccessPolicy
            : new AccessPolicy { AllowAnonymous = false, AllowAnonymousWrite = false };

        MetadataV2Field[] leftFields =
        [
            new() { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false, SemanticRoles = ["id.primary"] },
            new() { Name = "name", Type = MetadataV2FieldType.String, Length = 255 },
            new() { Name = "category", Type = MetadataV2FieldType.String, Length = 255 },
            new() { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true, SemanticRoles = ["geometry"] }
        ];

        MetadataV2Field[] rightFields =
        [
            new() { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false, SemanticRoles = ["id.primary"] },
            new() { Name = "id", Type = MetadataV2FieldType.String, Length = 255 },
            new() { Name = "name", Type = MetadataV2FieldType.String, Length = 255 },
        ];

        return new TestMetadataV2GraphBuilder()
            .AddConnection(WorkspaceId, "Analytics Workspace", provider: "postgres")
            .AddService(
                "svc-join-map",
                ServiceName,
                route: $"/rest/services/{ServiceName}/MapServer",
                protocols: [MetadataV2ServiceProtocols.MapServer],
                accessPolicy: openAccessPolicy)
            .AddResource(
                "res-left",
                "left_parcels",
                MetadataV2ResourceType.FeatureDataset,
                fields: leftFields,
                accessPolicy: openAccessPolicy)
            .AddStorageBinding(
                "binding-left",
                "res-left",
                "public.left_parcels",
                connectionId: WorkspaceId,
                storageLayerId: LeftStorageId,
                options: BindingOptions("left_parcels"))
            .AddPublication(
                id: "pub-left",
                serviceId: "svc-join-map",
                resourceId: "res-left",
                layerIndex: LeftLayerId,
                storageBindingId: "binding-left",
                serviceLocalId: LeftLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                publicationType: MetadataV2PublicationType.EsriMapLayer)
            .AddResource(
                "res-right",
                "right_parcels",
                MetadataV2ResourceType.FeatureDataset,
                fields: rightFields,
                accessPolicy: rightAccessPolicy)
            .AddStorageBinding(
                "binding-right",
                "res-right",
                "public.right_parcels",
                connectionId: WorkspaceId,
                storageLayerId: RightStorageId,
                options: BindingOptions("right_parcels"))
            .AddPublication(
                id: "pub-right",
                serviceId: "svc-join-map",
                resourceId: "res-right",
                layerIndex: RightLayerId,
                storageBindingId: "binding-right",
                serviceLocalId: RightLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                publicationType: MetadataV2PublicationType.EsriMapLayer)
            .BuildProvider();
    }

    private static Dictionary<string, JsonElement> BindingOptions(string tableName)
        => new()
        {
            ["schemaName"] = JsonSerializer.SerializeToElement("public"),
            ["tableName"] = JsonSerializer.SerializeToElement(tableName),
            ["geometryColumn"] = JsonSerializer.SerializeToElement("geometry")
        };
}
