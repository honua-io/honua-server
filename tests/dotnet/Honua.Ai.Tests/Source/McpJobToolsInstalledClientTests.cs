// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpJobToolsInstalledClientTests
{
    [ExternalServiceTest("HONUA_MCP_JOB_CLIENT")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task PublishedSdk_SubmitsPollsAndReadsGeometry_AndDeniesAnotherCaller()
    {
        await using var redis = new RedisFixture();
        await redis.InitializeAsync();
        await using var fixture = new WebAppFixture().UseKestrel().WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services => services.AddJobWorker());
        await fixture.InitializeAsync();
        using var admin = fixture.CreateAdminClient();
        using var keyResponse = await admin.PostAsJsonAsync("/api/v1/admin/api-keys", new
        {
            name = "job-result-reader",
            permissions = new[] { "admin:read" }
        });
        keyResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var keyBody = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
        var readerKey = keyBody.RootElement.GetProperty("data").GetProperty("key").GetString()!;

        var start = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.Resolve()
        };
        start.ArgumentList.Add(RepositoryPaths.Resolve("scripts", "qualification", "mcp", "job-results-journey.mjs"));
        start.ArgumentList.Add(admin.BaseAddress!.ToString());
        start.Environment["HONUA_MCP_JOB_OWNER_KEY"] = WebAppFixture.SharedAdminPassword;
        start.Environment["HONUA_MCP_JOB_OTHER_KEY"] = readerKey;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(75));
            process.ExitCode.Should().Be(0, await stderr);
            using var receipt = JsonDocument.Parse(await stdout);
            var result = receipt.RootElement;
            result.GetProperty("schema").GetString().Should().Be("honua.mcp-job-tools-journey/v1");
            result.GetProperty("sdkVersion").GetString().Should().Be("1.32.1");
            result.GetProperty("status").GetString().Should().Be("Succeeded");
            result.GetProperty("resultStatus").GetString().Should().Be("Completed");
            result.GetProperty("crossUserRead").GetString().Should().Be("not_found");
            result.GetProperty("crossUserListed").GetBoolean().Should().BeFalse();
            result.GetProperty("geometry").GetProperty("area").GetDouble().Should().BeInRange(
                Math.PI * 25.5 * 25.5 * 0.9, Math.PI * 25.5 * 25.5 * 1.01);
            var receipts = RepositoryPaths.Resolve("artifacts", "mcp-job-tools");
            Directory.CreateDirectory(receipts);
            await File.WriteAllTextAsync(Path.Join(receipts, "installed-client.json"), result.GetRawText());
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
}
