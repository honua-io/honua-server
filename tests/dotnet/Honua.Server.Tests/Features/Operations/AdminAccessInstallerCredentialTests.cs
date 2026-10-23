// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>
/// #3361 acceptance: the terminal journey verifies the installer-provisioned credential through the
/// canonical operations runtime. Each submission is a real Admin API round trip — the executor loops
/// back over Kestrel into the Admin REST route under the caller's own credential.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class AdminAccessInstallerCredentialTests
{
    private const string BootstrapKey = "lane-c-installer-bootstrap-key";

    [IntegrationTest]
    [Operation(Operations.ApiKeyManagement)]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("GET /api/v1/operations/handles/{handleId}")]
    public async Task InstallerCredential_ListsAndResolvesItsEffectivePermissions_ThroughCanonicalOperations()
    {
        var fixture = new WebAppFixture()
            .UseKestrel()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", BootstrapKey);
            });
        await fixture.InitializeAsync();
        try
        {
            // The installer provisions the terminal credential through the canonical Admin API.
            using var bootstrap = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", BootstrapKey));
            using var created = await bootstrap.PostAsJsonAsync(
                "/api/v1/admin/api-keys",
                new { name = $"installer-{Guid.NewGuid():N}", permissions = new[] { "admin:*" } });
            var createdBody = await created.Content.ReadAsStringAsync();
            created.StatusCode.Should().Be(HttpStatusCode.Created, createdBody);
            using var createdDocument = JsonDocument.Parse(createdBody);
            var installerKey = createdDocument.RootElement.GetProperty("data").GetProperty("key").GetString()!;
            var installerId = createdDocument.RootElement.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetString()!;

            using var terminal = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", installerKey));

            var list = await SubmitCompletedAsync(terminal, "admin.api-key.list", []);
            using (var listed = JsonDocument.Parse(ResponseOf(list)))
            {
                listed.RootElement.GetProperty("data").EnumerateArray()
                    .Select(static key => key.GetProperty("id").GetString())
                    .Should().Contain(installerId);
            }

            ResponseOf(list).Should().NotContain(installerKey, "listing keys must never return secret material");

            var effective = await SubmitCompletedAsync(
                terminal,
                "admin.api-key.effective-permissions",
                new Dictionary<string, string?> { ["id"] = installerId });
            using (var permissions = JsonDocument.Parse(ResponseOf(effective)))
            {
                var data = permissions.RootElement.GetProperty("data");
                data.GetProperty("id").GetString().Should().Be(installerId);
                data.GetProperty("canAuthenticate").GetBoolean().Should().BeTrue();
                data.GetProperty("permissions").EnumerateArray().Select(static permission => permission.GetString())
                    .Should().Contain("admin:*");
            }

            // A generic client reads the same canonical identities back from the durable handle.
            var handleId = effective.GetProperty("operationInstanceId").GetString()!;
            using var status = await terminal.GetAsync($"/api/v1/operations/handles/{handleId}");
            var statusBody = await status.Content.ReadAsStringAsync();
            status.StatusCode.Should().Be(HttpStatusCode.OK, statusBody);
            using var statusDocument = JsonDocument.Parse(statusBody);
            var statusData = statusDocument.RootElement.GetProperty("data");
            statusData.GetProperty("status").GetString().Should().Be("Completed");
            statusData.GetProperty("operationId").GetString().Should().Be("admin.api-key.effective-permissions");
            statusData.GetProperty("correlationId").GetString().Should().Be(effective.GetProperty("correlationId").GetString());

            // The host-composed projection (its catalog, approval mappers and actuators) keeps the installer
            // checks eligible and never advertises key issuance. Serving it over /mcp is #3363's publication
            // step, so the projection is enabled here rather than in host configuration.
            var source = new PublishedOperationToolSource(
                fixture.Services.GetRequiredService<IOperationCatalog>(),
                Options.Create(new McpPublishedOperationOptions { Enabled = true }),
                NullLogger<PublishedOperationToolSource>.Instance,
                fixture.Services.GetRequiredService<IServiceScopeFactory>(),
                fixture.Services.GetServices<IOperationApprovalRequestMapper>());
            var published = (await source.GetToolsAsync(CancellationToken.None)).Select(static tool => tool.Name).ToArray();
            published.Should().Contain(["honua_admin_api_key_list", "honua_admin_api_key_effective_permissions"]);
            published.Should().NotContain(["honua_admin_api_key_create", "honua_admin_api_key_rotate"]);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<JsonElement> SubmitCompletedAsync(
        HttpClient client,
        string operationId,
        Dictionary<string, string?> parameters)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/operations/{operationId}/submit", new { parameters });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var handle = document.RootElement.GetProperty("data").Clone();
        handle.GetProperty("status").GetString().Should().Be("Completed", body);
        return handle;
    }

    private static string ResponseOf(JsonElement handle)
        => handle.GetProperty("result").GetProperty("details").GetProperty("response").GetString()!;
}
