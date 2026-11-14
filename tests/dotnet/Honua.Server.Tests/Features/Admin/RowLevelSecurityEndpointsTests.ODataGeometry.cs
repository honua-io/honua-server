// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class RowLevelSecurityEndpointsTests
{
    private const long GeometryIntentObjectId = 901;
    private const string GeometryIntentPath = "/odata/Features(LayerId=0,ObjectId=901)";

    [IntegrationTheory]
    [InlineData("patch-omitted", false)]
    [InlineData("patch-omitted", true)]
    [InlineData("patch-null", false)]
    [InlineData("patch-null", true)]
    [InlineData("put-omitted", false)]
    [InlineData("put-omitted", true)]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    [Endpoint("PUT /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    [Endpoint("POST /odata/$batch")]
    public async Task ODataGeometryIntent_UnprojectedStoredGeometryRespectsMutationIntent(string mode, bool batch)
    {
        await PrepareGeometryIntentAsync();
        using var client = CreateGeometryIntentClient();
        using var read = await client.GetAsync(GeometryIntentPath);
        read.StatusCode.Should().Be(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("Geometry").ValueKind.Should().Be(JsonValueKind.Null);
        var body = new Dictionary<string, object?> { ["Attributes"] = new { name = "after" } };
        if (mode == "patch-null")
        {
            body["Geometry"] = null;
        }
        var method = mode == "put-omitted" ? "PUT" : "PATCH";

        Func<HttpClient, GeometryMutation, Task> sendMutation = batch
            ? SendBatchGeometryMutationAsync
            : SendDirectGeometryMutationAsync;
        await sendMutation(client, new GeometryMutation(method, body));

        var stored = await ReadGeometryIntentAsync();
        stored.Geometry.Should().Be(mode == "patch-omitted" ? "POINT(1 1)" : null);
        stored.Name.Should().Be("after");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Update)]
    [Endpoint("POST /odata/$batch")]
    public async Task ODataGeometryIntent_StagedExplicitGeometryThenOmissionKeepsLatestValue(bool clear)
    {
        await PrepareGeometryIntentAsync();
        using var client = CreateGeometryIntentClient();
        var first = new Dictionary<string, object?>
        {
            ["Attributes"] = new { name = "first" },
            ["Geometry"] = clear ? null : new { type = "Point", coordinates = new[] { 9.0, 9.0 } }
        };
        var second = new Dictionary<string, object?> { ["Attributes"] = new { name = "second" } };
        using var response = await client.PostAsJsonAsync("/odata/$batch", new
        {
            requests = new[] { GeometryBatchItem("first", "PATCH", first), GeometryBatchItem("second", "PATCH", second) }
        });
        await AssertGeometryBatchSuccessAsync(response, 2);

        var stored = await ReadGeometryIntentAsync();
        stored.Geometry.Should().Be(clear ? null : "POINT(9 9)");
        stored.Name.Should().Be("second");
    }

    private sealed record GeometryMutation(string Method, Dictionary<string, object?> Body);

    private static async Task SendBatchGeometryMutationAsync(HttpClient client, GeometryMutation mutation)
    {
        using var response = await client.PostAsJsonAsync("/odata/$batch", new
        {
            requests = new[] { GeometryBatchItem("edit", mutation.Method, mutation.Body) }
        });
        await AssertGeometryBatchSuccessAsync(response, 1);
    }

    private static async Task SendDirectGeometryMutationAsync(HttpClient client, GeometryMutation mutation)
    {
        using var request = new HttpRequestMessage(new HttpMethod(mutation.Method), GeometryIntentPath)
        {
            Content = JsonContent.Create(mutation.Body)
        };
        using var direct = await client.SendAsync(request);
        direct.IsSuccessStatusCode.Should().BeTrue(await direct.Content.ReadAsStringAsync());
    }

    private static object GeometryBatchItem(string id, string method, Dictionary<string, object?> body)
        => new
        {
            id,
            method,
            url = GeometryIntentPath,
            atomicityGroup = "geometry",
            body,
            headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
        };

    private static async Task AssertGeometryBatchSuccessAsync(HttpResponseMessage response, int expectedCount)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var responses = document.RootElement.GetProperty("responses").EnumerateArray().ToArray();
        responses.Should().HaveCount(expectedCount);
        responses.Select(item => item.GetProperty("status").GetInt32())
            .Should().OnlyContain(status => status >= 200 && status < 300, body);
    }

    private HttpClient CreateGeometryIntentClient()
    {
        var client = CreateAdminClient();
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "geometry-admin");
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, "admin");
        return client;
    }

    private async Task PrepareGeometryIntentAsync()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot();
        var publication = original.Graph.Publications.First(candidate =>
            candidate.PublicationType == MetadataV2LayerType.EsriFeatureLayer && candidate.LayerIndex == 0);
        var resource = original.ResolveResource(publication)!;
        var binding = original.ResolveStorageBinding(publication)!;
        var service = original.Index.ServicesById[publication.ServiceId] with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "geometry-intent-odata", Name = "geometry-intent-odata" },
            Protocols = [ServiceProtocols.OData]
        };
        var graph = original.Graph with
        {
            Revision = original.Graph.Revision + 1,
            Services = [.. original.Graph.Services, service],
            Publications =
            [
                publication with
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "geometry-intent-publication" },
                    ServiceId = service.Metadata.Id,
                    PublicationType = MetadataV2LayerType.ODataEntitySet,
                    StorageBindingId = binding.Metadata.Id,
                    Capabilities = ["Query", "Create", "Update", "Delete"]
                },
                .. original.Graph.Publications
            ],
            Resources = original.Graph.Resources.Select(item => item.Metadata.Id == resource.Metadata.Id ? item with
            {
                SchemaFields = item.SchemaFields.Where(field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)).ToArray(),
                Spatial = item.Spatial is null ? null : item.Spatial with { PrimaryGeometryField = null }
            } : item).ToArray(),
            StorageBindings = original.Graph.StorageBindings.Select(item => item.Metadata.Id == binding.Metadata.Id
                ? item with
                {
                    Options = item.Options.Where(pair => !pair.Key.Equals("geometryColumn", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                }
                : item).ToArray()
        };
        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeTrue();
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(graph, schema: _fixture.MetadataGraphSchema);
        await _fixture.Postgres.ExecuteAsync($$"""
            INSERT INTO "{{_fixture.CurrentSchema}}".features (layer_id, objectid, geometry, attributes)
            VALUES (0, {{GeometryIntentObjectId}}, ST_SetSRID(ST_MakePoint(1,1),4326), '{"name":"before"}');
            """);
    }

    private sealed record StoredGeometryIntent(string? Geometry, string? Name);

    private async Task<StoredGeometryIntent> ReadGeometryIntentAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.Postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT ST_AsText(geometry), attributes->>'name' FROM \"{_fixture.CurrentSchema}\".features WHERE layer_id = 0 AND objectid = {GeometryIntentObjectId}", connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new StoredGeometryIntent(reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
