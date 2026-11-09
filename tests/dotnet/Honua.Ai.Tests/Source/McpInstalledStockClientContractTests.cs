// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpConnectionDiscoveryIntegrationTests
{
    [ExternalServiceTest("HONUA_MCP_STOCK_CLIENT")]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /mcp")]
    public Task PublishedSdk_AllViews_ReadsAndRefusals_PreserveBothChannels() => RunStockContractAsync("sdk", "1.32.1");

    [ExternalServiceTest("HONUA_MCP_STOCK_CLIENT")]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /mcp")]
    public Task PublishedInspector_AllViews_ReadsAndRefusals_PreserveBothChannels() => RunStockContractAsync("inspector", "2.10.1");

    private async Task RunStockContractAsync(string kind, string version)
    {
        await using var fixture = CreateStockFixture();
        await fixture.InitializeAsync();
        using var admin = fixture.CreateAdminClient();
        using var process = Process.Start(await PrepareContractProcessAsync(kind, admin))!;
        await CaptureContractReceiptAsync(process, kind, version);
    }

    private static async Task<ProcessStartInfo> PrepareContractProcessAsync(string kind, HttpClient admin)
    {
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.Resolve()
        };
        start.ArgumentList.Add(RepositoryPaths.Resolve("scripts", "qualification", "mcp", "stock-client-contract.mjs"));
        start.ArgumentList.Add(kind);
        start.ArgumentList.Add(admin.BaseAddress!.ToString());
        start.Environment["HONUA_MCP_STOCK_KEY"] = WebAppFixture.SharedAdminPassword;
        if (kind == "inspector")
        {
            start.Environment["HONUA_MCP_STOCK_RATE_BUDGET"] = await ReadInspectorRateBudgetAsync(admin);
        }
        return start;
    }

    private static async Task CaptureContractReceiptAsync(Process process, string kind, string version)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(18));
            process.ExitCode.Should().Be(0, await stderr);
            using var receipt = JsonDocument.Parse(await stdout);
            var result = receipt.RootElement;
            AssertContractReceipt(result, kind, version);
            var directory = RepositoryPaths.Resolve("artifacts", "mcp-stock-clients");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Join(directory, $"{kind}.json"), result.GetRawText());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static void AssertContractReceipt(JsonElement result, string kind, string version)
    {
        result.GetProperty("schema").GetString().Should().Be("honua.mcp-catalog-channel-contract/v1");
        result.GetProperty("client").GetString().Should().Be(kind);
        result.GetProperty(kind == "sdk" ? "sdkVersion" : "inspectorVersion").GetString().Should().Be(version);
        result.GetProperty("largestSuccessfulPayloadBytes").GetInt32().Should().BeGreaterThanOrEqualTo(1024);
        result.GetProperty("views").EnumerateArray().Select(view => view.GetProperty("view").GetString())
            .Should().BeEquivalentTo(McpWorkflowViewCatalog.All.Keys.Append("full"));
        if (kind == "inspector")
        {
            result.GetProperty("rateBudget").GetProperty("waits").GetInt32().Should().BePositive();
        }
    }

    private static async Task<string> ReadInspectorRateBudgetAsync(HttpClient admin)
    {
        using var response = await admin.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list",
            @params = new { view = "full" }
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("result", out var result).Should().BeTrue("the rate probe must return a real discovery page");
        var pageSize = result.GetProperty("tools").GetArrayLength();
        pageSize.Should().BePositive();
        return JsonSerializer.Serialize(new
        {
            limit = int.Parse(response.Headers.GetValues("X-RateLimit-Limit").Single(), CultureInfo.InvariantCulture),
            resetAt = long.Parse(response.Headers.GetValues("X-RateLimit-Reset").Single(), CultureInfo.InvariantCulture),
            pageSize
        });
    }
}
