// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.OperationsToolset;

// admin.exports.* run as MCP tools through the canonical operation path (#9445): an administrator lists
// Exports through MCP and a caller without export authority is refused on every export tool.
[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Mcp)]
public sealed class ExportMcpToolsIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ExportTools_AdminLists_AndCallerWithoutExportAuthorityIsRefused()
    {
        var fixture = new WebAppFixture().WithProductionJobSubstrate()
            .UseKestrel()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });
        await fixture.InitializeAsync();
        try
        {
            using var admin = fixture.CreateAdminClient();
            var listed = await CallAsync(admin, "honua_admin_exports_list", new { });
            var structured = listed.GetProperty("result").GetProperty("structuredContent");
            structured.GetProperty("status").GetString().Should().Be("Completed", structured.GetRawText());
            structured.GetProperty("details").GetProperty("response").GetString().Should().NotBeNullOrWhiteSpace();

            using var reader = await ReaderClientAsync(fixture, admin);
            foreach (var (tool, arguments) in new (string, object)[]
                     {
                         ("honua_admin_exports_list", new { }),
                         ("honua_admin_exports_get", new { exportId = "parcels" }),
                         ("honua_admin_exports_create", new { source = new { kind = "layer", serviceId = "test", layerId = 0 } }),
                         ("honua_admin_exports_delete", new { exportId = "parcels" }),
                     })
            {
                var denied = await CallAsync(reader, tool, arguments);
                denied.GetRawText().Should().Contain("permission_denied", tool);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<HttpClient> ReaderClientAsync(WebAppFixture fixture, HttpClient admin)
    {
        using var created = await admin.PostAsJsonAsync(
            "/api/v1/admin/api-keys", new { name = $"reader-{Guid.NewGuid():N}", permissions = new[] { "read:layers" } });
        var body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        var key = document.RootElement.GetProperty("data").GetProperty("key").GetString()!;
        return fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));
    }

    private static async Task<JsonElement> CallAsync(HttpClient client, string name, object arguments)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name, arguments },
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mcp", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
