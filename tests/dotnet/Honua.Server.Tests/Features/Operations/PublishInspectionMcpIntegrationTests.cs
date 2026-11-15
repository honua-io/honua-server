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

// The read-only Publish inspection tools run through MCP tools/call with admin authority and
// refuse a caller without it; the loopback dispatch returns the endpoint's own payload.
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Mcp)]
public sealed class PublishInspectionMcpIntegrationTests(RedisFixture redis)
{
    [IntegrationTheory]
    [InlineData("honua_admin_import_formats")]
    [InlineData("honua_admin_import_limits")]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp")]
    public async Task InspectionTool_AdminRuns_AndReaderIsRefused(string tool)
    {
        var fixture = new WebAppFixture().WithProductionJobSubstrate()
            .UseKestrel()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });
        await fixture.InitializeAsync();
        try
        {
            using var admin = fixture.CreateAdminClient();
            var completed = await CallAsync(admin, tool);
            var structured = completed.GetProperty("result").GetProperty("structuredContent");
            structured.GetProperty("status").GetString().Should().Be("Completed", structured.GetRawText());
            structured.GetProperty("details").GetProperty("response").GetString().Should().NotBeNullOrWhiteSpace();

            using var reader = await ReaderClientAsync(fixture, admin);
            var denied = await CallAsync(reader, tool);
            denied.GetRawText().Should().Contain("permission_denied");
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

    private static async Task<JsonElement> CallAsync(HttpClient client, string name)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name, arguments = new { } },
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mcp", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
