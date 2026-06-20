// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.MapServer;
using Honua.Protocols.GeoServices.MapServer.Models;
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
/// Integration coverage for MapServer <c>dynamicLayers</c> workspace data layers
/// (<c>source.type=dataLayer</c>, #1660). A dataLayer source resolves a registered workspace
/// (data connection) and table to an already-published layer, gated by a server-side allowlist;
/// non-allowlisted workspaces, unpublished tables, and disabled servers are rejected.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerDynamicWorkspaceTests
{
    private const int WorkspaceLayerId = 0;
    private const string ServiceName = "workspace-map";
    private const string WorkspaceId = "analytics-workspace";
    private const string TableName = "parcels";
    private const string SchemaName = "public";

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task DynamicLayer_WithAllowlistedWorkspaceDataLayer_ResolvesPublishedLayer()
    {
        using var factory = CreateFactory(workspaceLayersEnabled: true, allowlist: [WorkspaceId]);
        using var client = factory.CreateClient();

        var layer = Uri.EscapeDataString(BuildDataLayerJson(TableName, SchemaName));
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/dynamicLayer?f=json&layer={layer}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("id").GetInt32().Should().Be(42);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task DynamicLayer_WithNonAllowlistedWorkspace_ReturnsBadRequest()
    {
        using var factory = CreateFactory(workspaceLayersEnabled: true, allowlist: ["some-other-workspace"]);
        using var client = factory.CreateClient();

        var layer = Uri.EscapeDataString(BuildDataLayerJson(TableName, SchemaName));
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/dynamicLayer?f=json&layer={layer}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("workspace that is not available");
        // The error must not disclose the connection string or table internals.
        content.Should().NotContain(WorkspaceId);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task DynamicLayer_WithWorkspaceLayersDisabled_ReturnsBadRequest()
    {
        using var factory = CreateFactory(workspaceLayersEnabled: false, allowlist: [WorkspaceId]);
        using var client = factory.CreateClient();

        var layer = Uri.EscapeDataString(BuildDataLayerJson(TableName, SchemaName));
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/dynamicLayer?f=json&layer={layer}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("not enabled on this server");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task DynamicLayer_WithUnpublishedTable_ReturnsBadRequest()
    {
        using var factory = CreateFactory(workspaceLayersEnabled: true, allowlist: [WorkspaceId]);
        using var client = factory.CreateClient();

        var layer = Uri.EscapeDataString(BuildDataLayerJson("not_published", SchemaName));
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/dynamicLayer?f=json&layer={layer}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("table that is not published");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task DynamicLayer_WithQueryTableDataSource_ReturnsBadRequest()
    {
        // queryTable (raw SQL query table) remains deferred (#1866) and must be rejected.
        using var factory = CreateFactory(workspaceLayersEnabled: true, allowlist: [WorkspaceId]);
        using var client = factory.CreateClient();

        // A queryTable carries a raw SQL string; it is rejected either by the request-level
        // injection guard or by the dataSource type check. Either way it must never resolve.
        var layer = Uri.EscapeDataString(
            "{\"id\":42,\"source\":{\"type\":\"dataLayer\",\"dataSource\":{\"type\":\"queryTable\",\"workspaceId\":\""
            + WorkspaceId + "\",\"oidFields\":\"objectid\"}}}");
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/dynamicLayer?f=json&layer={layer}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("not supported");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/dynamicLayer")]
    public async Task DynamicLayer_WithJoinTableMissingRightSource_ReturnsBadRequest()
    {
        using var factory = CreateFactory(workspaceLayersEnabled: true, allowlist: [WorkspaceId]);
        using var client = factory.CreateClient();

        var layer = Uri.EscapeDataString(
            "{\"id\":42,\"source\":{\"type\":\"dataLayer\",\"dataSource\":{\"type\":\"joinTable\","
            + "\"leftTableSource\":{\"type\":\"mapLayer\",\"mapLayerId\":0},"
            + "\"leftTableKey\":\"objectid\",\"rightTableKey\":\"objectid\"}}}");
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/dynamicLayer?f=json&layer={layer}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("rightTableSource");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task Export_WithNonAllowlistedWorkspaceDataLayer_ReturnsBadRequest()
    {
        using var factory = CreateFactory(workspaceLayersEnabled: true, allowlist: ["some-other-workspace"]);
        using var client = factory.CreateClient();

        var dynamicLayers = Uri.EscapeDataString(
            $"[{BuildDataLayerJson(TableName, SchemaName)}]");
        var response = await client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/export?bbox=-180,-90,180,90&size=64,64&f=json&dynamicLayers={dynamicLayers}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("workspace that is not available");
    }

    private static string BuildDataLayerJson(string tableName, string schemaName)
        => "{\"id\":42,\"source\":{\"type\":\"dataLayer\",\"dataSource\":{\"type\":\"table\",\"workspaceId\":\""
            + WorkspaceId + "\",\"dataSourceName\":\"" + tableName + "\",\"schema\":\"" + schemaName + "\"}}}";

    private static WebApplicationFactory<Program> CreateFactory(
        bool workspaceLayersEnabled,
        string[] allowlist)
        => new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton(_ => BuildWorkspaceGraphProvider());
                services.AddSingleton<IMetadataV2GraphProvider>(provider =>
                    provider.GetRequiredService<TestMetadataV2GraphProvider>());
                services.AddSingleton<IMetadataV2GraphStore>(provider =>
                    provider.GetRequiredService<TestMetadataV2GraphProvider>());

                services.Configure<MapServerDynamicLayersOptions>(options =>
                {
                    options.WorkspaceLayersEnabled = workspaceLayersEnabled;
                    options.AllowAllRegisteredWorkspaces = false;
                    options.AllowedWorkspaceIds.Clear();
                    foreach (var id in allowlist)
                    {
                        options.AllowedWorkspaceIds.Add(id);
                    }
                });
            });
        });

    private static TestMetadataV2GraphProvider BuildWorkspaceGraphProvider()
    {
        var openAccessPolicy = new AccessPolicy
        {
            AllowAnonymous = true,
            AllowAnonymousWrite = true
        };

        MetadataV2Field[] fields =
        [
            new() { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false, SemanticRoles = ["id.primary"] },
            new() { Name = "name", Type = MetadataV2FieldType.String, Length = 255 },
            new() { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true, SemanticRoles = ["geometry"] }
        ];

        return new TestMetadataV2GraphBuilder()
            .AddConnection(WorkspaceId, "Analytics Workspace", provider: "postgres")
            .AddService(
                "svc-workspace-map",
                ServiceName,
                route: $"/rest/services/{ServiceName}/MapServer",
                protocols: [MetadataV2ServiceProtocols.MapServer],
                accessPolicy: openAccessPolicy)
            .AddResource(
                "res-workspace-layer",
                "Parcels",
                MetadataV2ResourceType.FeatureDataset,
                fields: fields,
                accessPolicy: openAccessPolicy)
            .AddStorageBinding(
                "binding-workspace-layer",
                "res-workspace-layer",
                $"{SchemaName}.{TableName}",
                connectionId: WorkspaceId,
                storageLayerId: WorkspaceLayerId,
                options: new Dictionary<string, JsonElement>
                {
                    ["schemaName"] = JsonSerializer.SerializeToElement(SchemaName),
                    ["tableName"] = JsonSerializer.SerializeToElement(TableName),
                    ["geometryColumn"] = JsonSerializer.SerializeToElement("geometry")
                })
            .AddPublication(
                id: "pub-workspace-layer",
                serviceId: "svc-workspace-map",
                resourceId: "res-workspace-layer",
                layerIndex: WorkspaceLayerId,
                storageBindingId: "binding-workspace-layer",
                serviceLocalId: WorkspaceLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                publicationType: MetadataV2PublicationType.EsriMapLayer)
            .BuildProvider();
    }
}
