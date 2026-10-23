// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Security;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class FeatureServerEditForbiddenTransportTests : IAsyncLifetime
{
    private static readonly AccessPolicy ReaderPolicy = new()
    {
        AllowedRoles = ["reader", "data-editor"],
        AllowedWriteRoles = ["data-editor"]
    };
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Join("tests", "seed", "server.yaml"));
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceMetadata(0, accessPolicy: ReaderPolicy);
        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, accessPolicy: ReaderPolicy);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("addFeatures")]
    [InlineData("updateFeatures")]
    [InlineData("deleteFeatures")]
    [InlineData("applyEdits")]
    [InlineData("service-applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    public async Task ReadOnlyKey_DeniedEditHas403EnvelopeAndUnchangedRows(string operation)
    {
        var before = await ReadRowsAsync();
        before.Should().NotBe("[]", "the independent SQL control must contain seeded rows");
        var key = await CreateKeyAsync("read:test");
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);
        using var query = await client.GetAsync(QueryPath);
        query.StatusCode.Should().Be(HttpStatusCode.OK);
        using var queryBody = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        queryBody.RootElement.GetProperty("count").GetInt32().Should().Be(5);

        using var content = new StringContent(EditBody(operation), Encoding.UTF8, "application/json");
        using var denied = await client.PostAsync(EditPath(operation), content);
        await AssertErrorAsync(denied, HttpStatusCode.Forbidden, 403);
        (await ReadRowsAsync()).Should().Be(before);

        // The authorization refusal must precede body parsing on every registered mutation.
        using var malformed = new StringContent("not-json", Encoding.UTF8, "application/json");
        using var deniedMalformed = await client.PostAsync(EditPath(operation), malformed);
        await AssertErrorAsync(deniedMalformed, HttpStatusCode.Forbidden, 403);
        (await ReadRowsAsync()).Should().Be(before);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task ReadQuery_ForbiddenScopeRetains200And403Envelope()
    {
        var key = await CreateKeyAsync("read:unrelated-service");
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);
        using var denied = await client.GetAsync(QueryPath);
        await AssertErrorAsync(denied, HttpStatusCode.OK, 403);
    }

    [IntegrationTheory]
    [InlineData(false, 499)]
    [InlineData(true, 498)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    public async Task EditTokenChallenges_Retain200AndOriginalTokenCode(bool invalidToken, int code)
    {
        using var client = _fixture.CreateClient();
        var path = EditPath("updateFeatures") + (invalidToken ? "?token=invalid-token" : string.Empty);
        using var content = new StringContent("not-json", Encoding.UTF8, "application/json");
        using var denied = await client.PostAsync(path, content);
        await AssertErrorAsync(denied, HttpStatusCode.OK, code);
    }

    private static string QueryPath => $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json";

    private static string EditPath(string operation) => operation == "service-applyEdits"
        ? $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/applyEdits"
        : $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0/{operation}";

    private static string EditBody(string operation) => operation switch
    {
        "addFeatures" => "{\"features\":[{\"attributes\":{\"name\":\"Rejected add\"}}]}",
        "updateFeatures" => "{\"features\":[{\"attributes\":{\"objectid\":1,\"name\":\"Rejected update\"}}]}",
        "deleteFeatures" => "{\"objectIds\":\"1\"}",
        "service-applyEdits" => "{\"edits\":[{\"id\":0,\"updates\":[{\"attributes\":{\"objectid\":1,\"name\":\"Rejected update\"}}]}]}",
        _ => "{\"updates\":[{\"attributes\":{\"objectid\":1,\"name\":\"Rejected update\"}}]}"
    };

    private async Task<string> CreateKeyAsync(string permission)
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var created = await store.CreateAsync("read-only-edit-control", [permission],
            DateTimeOffset.UtcNow.AddMinutes(10), "test", CancellationToken.None);
        return created.Key;
    }

    private async Task<string> ReadRowsAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(jsonb_agg(to_jsonb(f) ORDER BY objectid), '[]'::jsonb)::text FROM features f WHERE layer_id = 0";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, int code)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(code, text);
        body.RootElement.TryGetProperty("updateResults", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("addResults", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("deleteResults", out _).Should().BeFalse();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }
}
