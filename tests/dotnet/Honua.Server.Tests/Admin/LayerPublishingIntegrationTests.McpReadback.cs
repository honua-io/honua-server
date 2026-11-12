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
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task McpPublishService_AgentRequestDoesNotPublish_HumanPublicationIsReadableByFreshClient()
    {
        await McpPublicationFixtureSetup.InstallPromotionStoresAsync(_fixture);
        using var errors = new McpPublicationErrorCapture();
        _fixture.Services.GetRequiredService<ILoggerFactory>().AddProvider(errors);
        await UseServerFeatureStoreConnectionAsync();
        var publishArguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["connectionId"] = _connectionId.ToString("D"),
            ["schema"] = _schema,
            ["table"] = _tableName,
            ["layerName"] = "MCP readback",
            ["serviceName"] = _serviceName,
            ["geometryColumn"] = "geom",
            ["geometryType"] = "Point",
            ["srid"] = 4326,
            ["primaryKey"] = "id",
            ["fields"] = _idNamePopulationFields,
        };

        // Every MCP caller is an agent: its publish needs one human approval and never executes directly.
        using var agentResponse = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "honua_publish_service", arguments = publishArguments }
        });
        var agentBody = await agentResponse.Content.ReadAsStringAsync();
        using (var agentDocument = JsonDocument.Parse(agentBody))
        {
            // This host has no durable proposal store, so the request fails closed instead of filing one.
            var refused = agentDocument.RootElement.GetProperty("result").GetProperty("structuredContent");
            refused.GetProperty("code").GetString().Should().Be("unavailable", agentBody);
            refused.GetProperty("capability").GetString().Should().Be("operations.proposals", agentBody);
        }

        using var publishResponse = await _client.PostAsJsonAsync("/api/v1/operations/service.publish/submit", new
        {
            connectionId = _connectionId.ToString("D"),
            serviceName = _serviceName,
            fields = _idNamePopulationFields,
            parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["schema"] = _schema,
                ["table"] = _tableName,
                ["layerName"] = "MCP readback",
                ["geometryColumn"] = "geom",
                ["geometryType"] = "Point",
                ["srid"] = "4326",
                ["primaryKey"] = "id",
            },
        });
        var publishBody = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.IsSuccessStatusCode.Should().BeTrue(publishBody);
        using var publishDocument = JsonDocument.Parse(publishBody);
        var details = publishDocument.RootElement.GetProperty("data").GetProperty("result").GetProperty("details");
        var layerId = int.Parse(details.GetProperty("layerId").GetString()!, CultureInfo.InvariantCulture);
        _layerId = layerId;
        var resourceUri = Honua.Ai.Protocols.Mcp.McpResourceUris.PublishedServiceUri(
            details.GetProperty("serviceName").GetString()!);

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
