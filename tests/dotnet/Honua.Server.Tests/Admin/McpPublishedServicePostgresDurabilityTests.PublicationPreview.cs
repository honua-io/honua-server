// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Admin;

public sealed partial class McpPublishedServicePostgresDurabilityTests
{
    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /mcp")]
    public async Task PublicationPreview_ReportsTheSourceAndMetadataBase_WithoutPublishing()
    {
        var revision = await ReadPersistedRevisionAsync();
        var sourceRows = await CountSourceRowsAsync(_firstTable);
        var result = await PreviewPublicationAsync();
        var handle = result.GetProperty("structuredContent");
        handle.GetProperty("status").GetString().Should().Be("Completed", handle.GetRawText());
        var details = handle.GetProperty("result").GetProperty("details");
        details.GetProperty("schema").GetString().Should().Be("public");
        details.GetProperty("table").GetString().Should().Be(_firstTable);
        details.GetProperty("serviceName").GetString().Should().Be(_serviceName);
        details.GetProperty("layerName").GetString().Should().Be("preview layer");
        details.GetProperty("metadataRevision").GetString().Should().Be(
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        details.GetProperty("metadataEtag").GetString().Should().NotBeNullOrWhiteSpace();
        (await PublishedLayerCountAsync(_firstTable)).Should().Be(0);
        (await ReadPersistedRevisionAsync()).Should().Be(revision);
        (await CountSourceRowsAsync(_firstTable)).Should().Be(sourceRows);
    }

    private async Task<JsonElement> PreviewPublicationAsync()
    {
        using var response = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "honua_op_service_publication_preview",
                arguments = new
                {
                    connectionId = _connectionId.ToString("D"),
                    schema = "public",
                    table = _firstTable,
                    layerName = "preview layer",
                    serviceName = _serviceName,
                    geometryColumn = "geom",
                    geometryType = "Point",
                    srid = 4326,
                    primaryKey = "id",
                    fields = new[] { "id", "name" }
                }
            }
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        var result = document.RootElement.GetProperty("result");
        result.TryGetProperty("isError", out var isError);
        (isError.ValueKind == JsonValueKind.True).Should().BeFalse(body);
        result.GetProperty("content").GetArrayLength().Should().BeGreaterThan(0);
        return result.Clone();
    }
}
