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
    public async Task PublicationPreview_ReferenceRejectsChangedFields_AndRequiresHumanApprovalForTheOriginal()
    {
        var preview = await PreviewPublicationAsync();
        var details = preview.GetProperty("structuredContent").GetProperty("details");
        var reference = details.GetProperty("previewReference").GetString()!;
        var revision = await ReadPersistedRevisionAsync();

        var changed = await ProposePreviewPublicationAsync(reference, ["id"]);
        var rejected = changed.TryGetProperty("error", out _) ||
            (changed.TryGetProperty("result", out var errorResult) &&
             errorResult.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True);
        rejected.Should().BeTrue(changed.GetRawText());

        var original = await ProposePreviewPublicationAsync(reference, ["id", "name"]);
        original.TryGetProperty("error", out _).Should().BeFalse(original.GetRawText());
        var proposal = original.GetProperty("result").GetProperty("structuredContent");
        proposal.GetProperty("status").GetString().Should().Be("RequiresApproval", original.GetRawText());
        proposal.GetProperty("proposalId").GetString().Should().NotBeNullOrWhiteSpace();
        (await PublishedLayerCountAsync(_firstTable)).Should().Be(0);
        (await ReadPersistedRevisionAsync()).Should().Be(revision);
    }

    private async Task<JsonElement> ProposePreviewPublicationAsync(string previewReference, string[] fields)
    {
        using var response = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "honua_publish_service",
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
                    fields,
                    previewReference
                }
            }
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
