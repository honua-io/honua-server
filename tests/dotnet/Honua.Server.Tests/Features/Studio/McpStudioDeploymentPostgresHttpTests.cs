// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Db.Postgres.Features.Publishing;
using Honua.Db.Postgres.Features.Studio;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// An actual Studio MCP publication creates the canonical deployment read by a new
/// MCP request, and a rebuilt host reads the same row from PostgreSQL.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpStudioDeploymentPostgresHttpTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private readonly RedisFixture _redis = new();

    public async Task InitializeAsync()
    {
        await _redis.InitializeAsync();
        _fixture.ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:redis", _redis.ConnectionString);
            builder.UseSetting("Licensing:Mode", "Disabled");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        _fixture.ConfigureServices(services =>
        {
            // TestKit re-registers the real Postgres provider after Program's feature
            // composition but leaves Database:Schema unset. Bind the production stores
            // to this fixture's retained schema before constructing MCP handlers.
            var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Missing fixture schema.");
            services.RemoveAll<IStudioPackageStore>();
            services.AddScoped<IStudioPackageStore>(provider => new PostgresStudioPackageStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(), schema));
            services.RemoveAll<IDeploymentStore>();
            services.AddSingleton<IDeploymentStore>(provider => new PostgresDeploymentStore(
                provider.GetRequiredService<NpgsqlDataSource>(), schema));
            services.RemoveAll<IPublishedServiceStore>();
            services.AddSingleton<IPublishedServiceStore>(provider => new PostgresPublishedServiceStore(
                provider.GetRequiredService<NpgsqlDataSource>(), schema));
            services.AddMcpPromotionSurface();
        });
        await _fixture.InitializeAsync();
        using var scope = _fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStudioPackageStore>()
            .Should().BeOfType<PostgresStudioPackageStore>();
        _fixture.Services.GetRequiredService<IDeploymentStore>()
            .Should().BeOfType<PostgresDeploymentStore>();
        await ApplyStudioMigrationsAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    [Endpoint("POST /mcp resources/read honua://deployments/{deploymentId}")]
    public async Task PublishedMap_ExactReturnedDeploymentResourceSurvivesHostRestart()
    {
        using var admin = _fixture.CreateAdminClient();
        var packageKey = $"deployment-map-{Guid.NewGuid():N}";
        var route = $"/maps/{packageKey}";
        var created = await CallToolAsync(admin, "honua_studio_create_draft",
            $$"""{"packageKey":"{{packageKey}}","family":"map","schemaVersion":"1.0"}""");
        var draftId = created.GetProperty("draftId").GetGuid();
        var updated = await CallToolAsync(admin, "honua_studio_update_draft",
            $$$"""{"body":{"layers":[{"id":"roads"}],"view":{"center":[-158,22],"zoom":7}},"draftId":"{{{draftId:D}}}","generation":1,"packageKey":"{{{packageKey}}}","schemaVersion":"1.0"}""");
        var generation = updated.GetProperty("generation").GetInt64();
        await CallToolAsync(admin, "honua_studio_validate_draft",
            $$"""{"draftId":"{{draftId:D}}"}""");
        var saved = await CallToolAsync(admin, "honua_studio_save_version",
            $$"""{"draftId":"{{draftId:D}}","generation":{{generation}}}""");
        var version = saved.GetProperty("version");
        var itemId = version.GetProperty("itemId").GetGuid();
        var versionId = version.GetProperty("versionId").GetGuid();
        var contentHash = version.GetProperty("contentHash").GetString();

        var published = await CallToolAsync(admin, "honua_studio_propose_publication",
            $$"""{"itemId":"{{itemId:D}}","versionId":"{{versionId:D}}","contentHash":"{{contentHash}}","route":"{{route}}","visibility":"public"}""");
        published.GetProperty("status").GetString().Should().Be("Published");
        var deploymentId = published.GetProperty("deploymentId").GetString();
        var resourceUri = published.GetProperty("deploymentResourceUri").GetString();
        deploymentId.Should().NotBeNullOrWhiteSpace();
        resourceUri.Should().Be($"honua://deployments/{deploymentId}");

        var firstRead = await ReadDeploymentAsync(admin, resourceUri!);
        AssertDeployment(firstRead, deploymentId!, itemId, versionId, contentHash!);
        await AssertPersistedRowAsync(deploymentId!);

        await _fixture.RestartHostAsync();
        using var restartedAdmin = _fixture.CreateAdminClient();
        var restartedRead = await ReadDeploymentAsync(restartedAdmin, resourceUri!);
        AssertDeployment(restartedRead, deploymentId!, itemId, versionId, contentHash!);
        restartedRead.GetProperty("etag").GetString().Should().Be(firstRead.GetProperty("etag").GetString());

        using var anonymous = _fixture.CreateClient();
        using var denied = await PostRpcAsync(anonymous,
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "anon", method = "resources/read", @params = new { uri = resourceUri } }));
        var denial = await denied.Content.ReadAsStringAsync();
        if (denied.StatusCode == HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(denial);
            document.RootElement.GetProperty("error").GetProperty("data").GetProperty("code")
                .GetString().Should().Be("unauthenticated");
        }
        else
        {
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized, denial);
        }
    }

    private async Task ApplyStudioMigrationsAsync()
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Missing fixture schema.");
        await _fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
        {
            foreach (var file in new[]
                     {
                         "035_CreateStudioPackageLifecycle.sql",
                         "036_CreateContentPublications.sql",
                         "082_CreatePromotionStores.sql",
                         "089_AddStudioContentEnumerationIndexes.sql",
                         "090_AddStudioContentItemOwner.sql",
                         "092_CreateSavedMapOperationLog.sql",
                         "093_EnsureConfiguredStudioPackageLifecycle.sql",
                         "094_CreateSavedMapCheckpointVersionRecovery.sql",
                         "095_DeleteSavedMapOperationLogWithStudioDraft.sql",
                         "120_AddStudioTenantOwnership.sql",
                         "126_AddStudioContentHashInput.sql",
                         "134_VersionCanonicalDeployments.sql",
                     })
            {
                await using var stream = typeof(Program).Assembly.GetManifestResourceStream($"Honua.Server.Migrations.{file}")
                    ?? throw new InvalidOperationException($"Missing embedded migration {file}.");
                using var reader = new StreamReader(stream);
                var sql = (await reader.ReadToEndAsync())
                    .Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
                if (file == "082_CreatePromotionStores.sql")
                {
                    sql = sql.Replace("honua.", $"\"{schema}\".", StringComparison.Ordinal);
                }

                await _fixture.Postgres.ExecuteAsync(sql, schema);
            }
        });
    }

    private async Task AssertPersistedRowAsync(string deploymentId)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Missing fixture schema.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = new NpgsqlCommand(
            $"SELECT source_kind FROM \"{schema}\".promotion_deployments WHERE deployment_id = @deployment_id",
            connection);
        command.Parameters.AddWithValue("@deployment_id", deploymentId);
        (await command.ExecuteScalarAsync()).Should().Be("StudioPublishedVersion");
    }

    private static void AssertDeployment(JsonElement readback, string deploymentId, Guid itemId,
        Guid versionId, string contentHash)
    {
        readback.GetProperty("deploymentId").GetString().Should().Be(deploymentId);
        readback.GetProperty("sourceKind").GetString().Should().Be("StudioPublishedVersion");
        readback.GetProperty("studioItemId").GetGuid().Should().Be(itemId);
        readback.GetProperty("studioVersionId").GetGuid().Should().Be(versionId);
        readback.GetProperty("studioContentHash").GetString().Should().Be(contentHash);
        readback.GetProperty("status").GetString().Should().Be("Active");
        readback.GetProperty("runtimeHealth").GetString().Should().Be("Unknown");
    }

    private static async Task<JsonElement> ReadDeploymentAsync(HttpClient client, string uri)
    {
        var response = await RpcAsync(client,
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "read", method = "resources/read", @params = new { uri } }));
        response.TryGetProperty("error", out _).Should().BeFalse(response.GetRawText());
        var contents = response.GetProperty("result").GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);
        contents[0].GetProperty("uri").GetString().Should().Be(uri);
        using var document = JsonDocument.Parse(contents[0].GetProperty("text").GetString()!);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string name, string arguments)
    {
        var response = await RpcAsync(client,
            $$$"""{"jsonrpc":"2.0","id":"{{{name}}}","method":"tools/call","params":{"name":"{{{name}}}","arguments":{{{arguments}}}}}""");
        response.TryGetProperty("error", out _).Should().BeFalse(response.GetRawText());
        var result = response.GetProperty("result");
        result.TryGetProperty("isError", out var isError).Should().BeTrue(result.GetRawText());
        isError.GetBoolean().Should().BeFalse(result.GetRawText());
        return result.GetProperty("structuredContent").Clone();
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string body)
    {
        using var response = await PostRpcAsync(client, body);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PostRpcAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        return await client.SendAsync(request);
    }
}
