// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Db.Postgres.Features.Studio;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class StudioDashboardMcpIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private readonly string _studioSchema = $"dashboard_{Guid.NewGuid():N}";
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _fixture.ConfigureServices(services =>
        {
            services.RemoveAll<IStudioPackageStore>();
            services.AddScoped<IStudioPackageStore>(provider => new PostgresStudioPackageStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(), _studioSchema));
        });
        await _fixture.InitializeAsync();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Honua.sln")))
        {
            root = root.Parent;
        }
        root.Should().NotBeNull();
        foreach (var migration in new[]
                 {
                     "035_CreateStudioPackageLifecycle.sql", "036_CreateContentPublications.sql",
                     "089_AddStudioContentEnumerationIndexes.sql", "090_AddStudioContentItemOwner.sql",
                 })
        {
            var sql = await File.ReadAllTextAsync(Path.Join(root!.FullName, "src", "Honua.Server", "Migrations", migration));
            await _fixture.Postgres.ExecuteAsync(
                sql.Replace("$HonuaSchema$", $"\"{_studioSchema}\"", StringComparison.Ordinal));
        }
        _client = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _fixture.Postgres.DropSchemaAsync(_studioSchema);
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    public async Task Dashboard_AllElevenCompositionVerbs_PersistExpectedValuesAndRejectStaleWrites()
    {
        var packageKey = $"dashboard-{Guid.NewGuid():N}";
        var created = await CallAsync("create_draft",
            $$"""{"packageKey":"{{packageKey}}","family":"dashboard","schemaVersion":"1.0"}""");
        created.TryGetProperty("draftId", out var createdDraftId).Should().BeTrue(created.GetRawText());
        var draftId = createdDraftId.GetGuid();
        long generation = 1;

        async Task Mutate(string verb, string fields)
        {
            var result = await CallAsync(verb,
                $$"""{"draftId":"{{draftId}}","generation":{{generation}},{{fields}}}""");
            result.GetProperty("generation").GetInt64().Should().Be(++generation);
        }

        await Mutate("add_layer", "\"layer\":{\"id\":\"parcels\",\"type\":\"fill\"}");
        await Mutate("set_layer_style", "\"layerId\":\"parcels\",\"styleRef\":\"night\"");
        await Mutate("set_layer_visibility", "\"layerId\":\"parcels\",\"visible\":false");
        await Mutate("set_view", "\"view\":{\"center\":[-157.86,21.31],\"zoom\":10}");
        await Mutate("add_widget", "\"widget\":{\"id\":\"legend\",\"kind\":\"legend\"}");
        await Mutate("bind_interaction", "\"interaction\":{\"id\":\"select\",\"on\":{\"ref\":\"layer:parcels\",\"event\":\"featureSelect\"},\"do\":{\"ref\":\"map\",\"verb\":\"setViewport\"}}");
        await Mutate("add_control", "\"control\":{\"id\":\"scale\",\"kind\":\"navigation\"}");

        var loaded = await CallAsync("get_draft", $$"""{"draftId":"{{draftId}}"}""");
        var body = loaded.GetProperty("envelope").GetProperty("body");
        body.GetProperty("layers")[0].GetProperty("id").GetString().Should().Be("parcels");
        body.GetProperty("layers")[0].GetProperty("styleRef").GetString().Should().Be("night");
        body.GetProperty("layers")[0].GetProperty("visible").GetBoolean().Should().BeFalse();
        body.GetProperty("view").GetProperty("center").EnumerateArray().Select(v => v.GetDouble())
            .Should().Equal(-157.86, 21.31);
        body.GetProperty("view").GetProperty("zoom").GetDouble().Should().Be(10);
        body.GetProperty("widgets")[0].GetProperty("id").GetString().Should().Be("legend");
        body.GetProperty("interactions")[0].GetProperty("do").GetProperty("verb").GetString().Should().Be("setViewport");
        body.GetProperty("controls")[0].GetProperty("kind").GetString().Should().Be("navigation");

        // A second writer's stale generation cannot remove a layer that may have changed.
        var stale = await RpcAsync("remove_layer", $$"""{"draftId":"{{draftId}}","generation":1,"layerId":"parcels"}""");
        stale.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        stale.GetProperty("result").GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("failed_precondition");
        stale.GetProperty("result").GetProperty("structuredContent").GetProperty("currentGeneration").GetInt64().Should().Be(generation);
        var reread = await CallAsync("get_draft", $$"""{"draftId":"{{draftId}}"}""");
        reread.GetProperty("generation").GetInt64().Should().Be(generation);
        reread.GetProperty("envelope").GetProperty("body").GetProperty("layers").GetArrayLength().Should().Be(1);

        // A stale independent removal is retried only after checking its unchanged target.
        var staleControl = await RpcAsync("remove_control", $$"""{"draftId":"{{draftId}}","generation":1,"controlId":"scale"}""");
        staleControl.GetProperty("result").GetProperty("structuredContent").GetProperty("code")
            .GetString().Should().Be("failed_precondition");
        reread = await CallAsync("get_draft", $$"""{"draftId":"{{draftId}}"}""");
        reread.GetProperty("envelope").GetProperty("body").GetProperty("controls")[0]
            .GetProperty("id").GetString().Should().Be("scale");
        generation = reread.GetProperty("generation").GetInt64();
        var retryGeneration = generation;
        await Mutate("remove_control", "\"controlId\":\"scale\"");
        var duplicateRetry = await RpcAsync("remove_control",
            $$"""{"draftId":"{{draftId}}","generation":{{retryGeneration}},"controlId":"scale"}""");
        duplicateRetry.GetProperty("result").GetProperty("structuredContent").GetProperty("code")
            .GetString().Should().Be("failed_precondition");
        await Mutate("remove_interaction", "\"interactionId\":\"select\"");
        await Mutate("remove_widget", "\"widgetId\":\"legend\"");
        await Mutate("remove_layer", "\"layerId\":\"parcels\"");
        var final = await CallAsync("get_draft", $$"""{"draftId":"{{draftId}}"}""");
        final.GetProperty("generation").GetInt64().Should().Be(12);
        var finalBody = final.GetProperty("envelope").GetProperty("body");
        foreach (var collection in new[] { "layers", "widgets", "interactions", "controls" })
        {
            finalBody.GetProperty(collection).GetArrayLength().Should().Be(0);
        }

        await Mutate("update_draft", $"\"packageKey\":\"{packageKey}\",\"schemaVersion\":\"1.0\"," + "\"body\":{\"layers\":[{\"id\":\"roads\"}],\"view\":{\"center\":[-158,22],\"zoom\":7}}");
        var malformed = await RpcAsync("update_draft", $$$"""{"draftId":"{{{draftId}}}","generation":{{{generation}}},"packageKey":"{{{packageKey}}}","schemaVersion":"1.0","body":{"interactions":{"id":"invalid"} } }""");
        malformed.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        malformed.GetProperty("result").GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("invalid_argument");
        var afterRejected = await CallAsync("get_draft", $$"""{"draftId":"{{draftId}}"}""");
        afterRejected.GetProperty("generation").GetInt64().Should().Be(generation);
        afterRejected.GetProperty("envelope").GetProperty("body").GetProperty("layers")[0]
            .GetProperty("id").GetString().Should().Be("roads");

        await CallAsync("validate_draft", $$"""{"draftId":"{{draftId}}"}""");
        using var saveResponse = await _client.PostAsync(
            $"/api/v1/studio/package-drafts/{draftId:D}/content-versions",
            new StringContent("""{"changeNote":"dashboard fixture"}""", Encoding.UTF8, "application/json"));
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created, await saveResponse.Content.ReadAsStringAsync());
        using var savedDocument = JsonDocument.Parse(await saveResponse.Content.ReadAsStringAsync());
        var version = JsonSerializer.Deserialize(savedDocument.RootElement.GetProperty("data"),
            StudioJsonContext.Default.StudioContentVersion)!;
        version.Validation.Status.Should().Be(StudioPackageValidationStatus.Valid);

        // Independent fixture expectation: PostgreSQL jsonb orders these body keys by length.
        // Hash the declared expected document, never a copy of the server's returned envelope.
        using var expectedBody = JsonDocument.Parse("""{"view":{"zoom":7,"center":[-158,22]},"layers":[{"id":"roads"}]}""");
        var expectedEnvelope = new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Dashboard,
            SchemaVersion = "1.0",
            Format = "studio_dashboard_package.v1",
            Body = expectedBody.RootElement.Clone(),
            Validation = new StudioValidationSummary { Status = StudioPackageValidationStatus.Valid },
        };
        var expectedHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            expectedEnvelope, StudioJsonContext.Default.StudioPackageEnvelope))).ToLowerInvariant();
        version.ContentHash.Should().Be(expectedHash);

        // A second application host has its own lifecycle/runtime/store instances. Only the
        // database connection is shared; no draft or version object crosses the replica boundary.
        var connectionProvider = _fixture.Services.GetRequiredService<IAdoNetDatabaseConnectionProvider>();
        var replica = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IStudioPackageStore>();
            services.AddScoped<IStudioPackageStore>(_ => new PostgresStudioPackageStore(connectionProvider, _studioSchema));
        });
        await replica.InitializeAsync();
        try
        {
            using var replicaClient = replica.CreateAdminClient();
            var versionPath = $"/api/v1/studio/content-items/{version.ItemId:D}/versions/{version.VersionId:D}";
            using var getResponse = await replicaClient.GetAsync(versionPath);
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var reloadedDocument = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            var reloaded = reloadedDocument.RootElement.GetProperty("data");
            reloaded.GetProperty("versionId").GetGuid().Should().Be(version.VersionId);
            reloaded.GetProperty("contentHash").GetString().Should().Be(expectedHash);
            using var reopenResponse = await replicaClient.PostAsync(versionPath + "/reopen",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            reopenResponse.StatusCode.Should().Be(HttpStatusCode.Created, await reopenResponse.Content.ReadAsStringAsync());
            using var reopenedDocument = JsonDocument.Parse(await reopenResponse.Content.ReadAsStringAsync());
            var reopened = JsonSerializer.Deserialize(reopenedDocument.RootElement.GetProperty("data"),
                StudioJsonContext.Default.StudioPackageDraft)!;
            reopened.BaseVersionId.Should().Be(version.VersionId);
            StudioPackageHash.Compute(reopened.Envelope).Should().Be(expectedHash);
            await using var reader = replica.Services.CreateAsyncScope();
            var lifecycle = reader.ServiceProvider.GetRequiredService<IStudioPackageLifecycleService>();
            reader.ServiceProvider.GetRequiredService<IStudioPackageStore>().PersistenceMode
                .Should().Be(StudioPackagePersistenceMode.Durable);
            (await lifecycle.GetPointersAsync(version.ItemId))!.PublishedVersionId.Should().BeNull();
        }
        finally
        {
            await replica.DisposeAsync();
        }
    }

    private async Task<JsonElement> CallAsync(string verb, string arguments)
    {
        var response = await RpcAsync(verb, arguments);
        response.TryGetProperty("error", out _).Should().BeFalse(response.GetRawText());
        var result = response.GetProperty("result");
        if (result.TryGetProperty("isError", out var error))
        {
            error.GetBoolean().Should().BeFalse(result.GetRawText());
        }
        return result.GetProperty("structuredContent").Clone();
    }

    private async Task<JsonElement> RpcAsync(string verb, string arguments)
    {
        using var content = new StringContent(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"honua_studio_{{{verb}}}","arguments":{{{arguments}}}}}""",
            Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/mcp", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
