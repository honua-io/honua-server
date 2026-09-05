// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

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
        _fixture.ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Schema"] = _studioSchema })));
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
        stale.TryGetProperty("error", out _).Should().BeTrue();
        var reread = await CallAsync("get_draft", $$"""{"draftId":"{{draftId}}"}""");
        reread.GetProperty("generation").GetInt64().Should().Be(generation);
        reread.GetProperty("envelope").GetProperty("body").GetProperty("layers").GetArrayLength().Should().Be(1);

        // Re-read proves the independent control removal remains valid. Apply it once.
        await Mutate("remove_control", "\"controlId\":\"scale\"");
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
        malformed.TryGetProperty("error", out _).Should().BeTrue();
        var afterRejected = await CallAsync("get_draft", $$"""{"draftId":"{{draftId}}"}""");
        afterRejected.GetProperty("generation").GetInt64().Should().Be(generation);
        afterRejected.GetProperty("envelope").GetProperty("body").GetProperty("layers")[0]
            .GetProperty("id").GetString().Should().Be("roads");
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
