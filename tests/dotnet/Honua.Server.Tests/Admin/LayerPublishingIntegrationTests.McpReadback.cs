// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.Mcp)]
    [Operation(Operations.Create)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task McpPublishService_CompletedPublication_ReturnedResourceIsReadableByFreshClient()
    {
        await McpPublicationFixtureSetup.InstallPromotionStoresAsync(_fixture);
        using var errors = new McpPublicationErrorCapture();
        _fixture.Services.GetRequiredService<ILoggerFactory>().AddProvider(errors);
        await UseServerFeatureStoreConnectionAsync();
        using var publishResponse = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "honua_publish_service",
                arguments = new
                {
                    connectionId = _connectionId.ToString("D"),
                    schema = _schema,
                    table = _tableName,
                    layerName = "MCP readback",
                    serviceName = _serviceName,
                    geometryColumn = "geom",
                    geometryType = "Point",
                    srid = 4326,
                    primaryKey = "id",
                    fields = _idNamePopulationFields
                }
            }
        });
        var publishBody = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(HttpStatusCode.OK, publishBody);
        using var publishDocument = JsonDocument.Parse(publishBody);
        var publish = publishDocument.RootElement;
        publish.TryGetProperty("error", out _).Should().BeFalse(publishBody);
        var result = publish.GetProperty("result");
        result.TryGetProperty("isError", out var isError);
        (isError.ValueKind == JsonValueKind.True).Should().BeFalse(publishBody);
        var completed = result.GetProperty("structuredContent");
        completed.GetProperty("status").GetString().Should().Be("Completed", publishBody);
        var layerId = int.Parse(completed.GetProperty("layerId").GetString()!, CultureInfo.InvariantCulture);
        _layerId = layerId;
        var resourceUri = completed.GetProperty("serviceUri").GetString();
        resourceUri.Should().NotBeNullOrWhiteSpace();

        // The published source is real PostGIS data. Do not seed a separate
        // promotion record or replace the tool's URI with an inferred identity.
        (await GetSourceRowCountAsync()).Should().Be(1);
        await AssertPublishedCollectionServesSeedRowOnlyAsync(layerId);

        using var freshClient = _fixture.CreateAdminClient();
        using var readResponse = await freshClient.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "resources/read",
            @params = new { uri = resourceUri }
        });
        var readBody = await readResponse.Content.ReadAsStringAsync();
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK, readBody);
        using var readDocument = JsonDocument.Parse(readBody);
        var read = readDocument.RootElement;
        read.TryGetProperty("error", out _).Should().BeFalse("{0}{1}{2}", readBody, Environment.NewLine, errors.Diagnostics);
        var contents = read.GetProperty("result").GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);
        contents[0].GetProperty("uri").GetString().Should().Be(resourceUri);
        using var serviceDocument = JsonDocument.Parse(contents[0].GetProperty("text").GetString()!);
        var service = serviceDocument.RootElement;
        service.GetProperty("resourceUri").GetString().Should().Be(resourceUri);
        service.GetProperty("status").GetString().Should().Be("Active");
    }
}
