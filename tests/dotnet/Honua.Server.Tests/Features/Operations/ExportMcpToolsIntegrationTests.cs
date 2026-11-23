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

// admin.exports.* run as MCP tools through the canonical operation path: an administrator lists
// Exports through MCP and a caller without export authority is refused on every export tool.
[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Mcp)]
public sealed class ExportMcpToolsIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_admin_exports_list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ExportTools_AdminLists_AndCallerWithoutExportAuthorityIsRefused()
    {
        var fixture = await StartFixtureAsync();
        try
        {
            using var admin = fixture.CreateAdminClient();
            var listed = await CallAsync(admin, "honua_admin_exports_list", new { });
            var structured = listed.GetProperty("result").GetProperty("structuredContent");
            structured.GetProperty("status").GetString().Should().Be("Completed", structured.GetRawText());
            structured.GetProperty("details").GetProperty("response").GetString().Should().NotBeNullOrWhiteSpace();

            using var reader = await ReaderClientAsync(fixture, admin);
            foreach (var (tool, arguments) in ExportToolCalls())
            {
                var denied = await CallAsync(reader, tool, arguments);
                DeniedCode(denied).Should().Be("permission_denied", $"{tool}: {denied.GetRawText()}");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Creating or deleting an export always files a proposal for a person to approve, even for an
    // administrator's credential, and nothing is written until then.
    [IntegrationTheory]
    [InlineData("honua_admin_exports_create")]
    [InlineData("honua_admin_exports_delete")]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_admin_exports_create")]
    [Endpoint("POST /mcp tools/call honua_admin_exports_delete")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ExportWriteTools_FileAProposal_AndChangeNothingBeforeApproval(string tool)
    {
        var fixture = await StartFixtureAsync();
        try
        {
            using var admin = fixture.CreateAdminClient();
            var before = await ExportListResponseAsync(admin);
            var arguments = ExportToolCalls().Single(call => call.Tool == tool).Arguments;

            var filed = await CallAsync(admin, tool, arguments);
            var handle = filed.GetProperty("result").GetProperty("structuredContent");
            handle.GetProperty("status").GetString().Should().Be("RequiresApproval", filed.GetRawText());
            handle.GetProperty("proposalId").GetString().Should().NotBeNullOrWhiteSpace(filed.GetRawText());
            (await ExportListResponseAsync(admin)).Should().Be(before, "an export write waits for a person");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static (string Tool, object Arguments)[] ExportToolCalls() =>
    [
        ("honua_admin_exports_list", new { }),
        ("honua_admin_exports_get", new { exportId = "parcels" }),
        ("honua_admin_exports_create", new { source = new { kind = "layer", serviceId = "test", layerId = 0 } }),
        ("honua_admin_exports_delete", new { exportId = "parcels" }),
    ];

    private static async Task<WebAppFixture> StartFixtureAsync()
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
        return fixture;
    }

    // A refusal arrives as a JSON-RPC error or as an error tool result; both carry the code.
    private static string? DeniedCode(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error)
            && error.TryGetProperty("data", out var data)
            && data.TryGetProperty("code", out var code))
        {
            return code.GetString();
        }

        return root.TryGetProperty("result", out var result)
            && result.TryGetProperty("structuredContent", out var structured)
            && structured.TryGetProperty("code", out var resultCode)
                ? resultCode.GetString()
                : null;
    }

    private static async Task<string?> ExportListResponseAsync(HttpClient admin)
    {
        var listed = await CallAsync(admin, "honua_admin_exports_list", new { });
        return listed.GetProperty("result").GetProperty("structuredContent").GetProperty("details")
            .GetProperty("response").GetString();
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
