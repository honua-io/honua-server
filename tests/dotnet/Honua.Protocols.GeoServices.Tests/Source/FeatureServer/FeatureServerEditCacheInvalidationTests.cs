// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Read-your-writes across the exact response cache (honua-server#4406, the missing test asset
/// behind #4259): with <c>Cache:ResponseCachingEnabled</c> turned on, a query issued after an
/// <c>applyEdits</c> must observe the edit immediately, not at the end of a cache TTL. Before this
/// class no test edited and then queried with response caching enabled at all — the nearest
/// coverage was a metadata-cache regression on the attachments endpoint.
/// </summary>
/// <remarks>
/// Each test first proves the cache is genuinely warm, so a configuration change that silently
/// stopped caching could not turn these into vacuous passes: it mutates the row <b>out of band</b>
/// with SQL (which the exact response cache is not expected to notice) and asserts the query still
/// replays the cached body. Only then does it perform the real edit and require the next query to
/// change.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerEditCacheInvalidationTests : IAsyncLifetime
{
    private const string ServiceId = "test";
    private const int LayerId = 0;

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("Cache:Enabled", "true");
            builder.UseSetting("Cache:ResponseCachingEnabled", "true");
            // ResponseCacheUtilities.ShouldCache skips authenticated requests, and the default
            // test host authenticates every request through the dev-auth bypass — which is why no
            // existing HTTP test could reach the exact response cache at all. Turn the bypass off
            // so these requests are anonymous and the cache is actually on the path.
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
        });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(ServiceId, ["Create", "Update", "Delete"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task ApplyEdits_UpdateThenQuery_ReturnsTheEditedValueWithoutWaitingOutTheCacheTtl()
    {
        var objectId = await AddFeatureAsync("cache-before");
        var query = $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/query?f=json&objectIds={objectId}&outFields=*";

        (await QueryNameAsync(query)).Should().Be("cache-before", "the first query populates the response cache");

        // Warmth check: an out-of-band write is invisible to an exact response cache, so a second
        // query returning the ORIGINAL value is the proof that the cache is actually serving this
        // request. If response caching were off, this assertion fails immediately and loudly.
        await _fixture.UpdateStoredFeatureNameAsync(LayerId, objectId, "cache-out-of-band");
        (await QueryNameAsync(query)).Should().Be(
            "cache-before",
            "an out-of-band SQL write does not invalidate the exact response cache, so this is the " +
            "assertion that proves the cached body is being replayed");

        // The real edit must invalidate.
        var edit = await PostApplyEditsAsync(
            """{"updates":[{"attributes":{"objectid":OBJECTID,"name":"cache-after"}}]}"""
                .Replace("OBJECTID", objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var editBody = await edit.Content.ReadAsStringAsync();
        edit.Be200Ok();
        Deserialize(editBody).UpdateResults
            .Should().ContainSingle(result => result.Success && result.ObjectId == objectId, editBody);

        (await QueryNameAsync(query)).Should().Be(
            "cache-after",
            "an edit through the shared pipeline must invalidate the response cache immediately — " +
            "read-your-writes cannot be deferred to the end of a TTL (#4259)");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task ApplyEdits_AddThenQuery_IncludesTheNewFeatureImmediately()
    {
        var marker = $"cache-add-{Guid.NewGuid():n}";
        var where = Uri.EscapeDataString($"name='{marker}'");
        var query = $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/query?f=json&where={where}&returnCountOnly=true";

        (await QueryCountAsync(query)).Should().Be(0, "the first query populates the response cache with an empty result");

        await AddFeatureAsync(marker);

        (await QueryCountAsync(query)).Should().Be(
            1,
            "a create must invalidate the cached empty result for the same query");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task ApplyEdits_DeleteThenQuery_DropsTheFeatureImmediately()
    {
        var marker = $"cache-delete-{Guid.NewGuid():n}";
        var objectId = await AddFeatureAsync(marker);
        var where = Uri.EscapeDataString($"name='{marker}'");
        var query = $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/query?f=json&where={where}&returnCountOnly=true";

        (await QueryCountAsync(query)).Should().Be(1);

        var delete = await PostApplyEditsAsync(
            """{"deletes":[OBJECTID]}"""
                .Replace("OBJECTID", objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var deleteBody = await delete.Content.ReadAsStringAsync();
        delete.Be200Ok();
        Deserialize(deleteBody).DeleteResults
            .Should().ContainSingle(result => result.Success && result.ObjectId == objectId, deleteBody);

        (await QueryCountAsync(query)).Should().Be(
            0,
            "a delete must invalidate the cached response that still contains the deleted feature");
    }

    private async Task<long> AddFeatureAsync(string name)
    {
        var response = await PostApplyEditsAsync(
            """{"adds":[{"attributes":{"name":"NAME"},"geometry":{"x":-122.4194,"y":37.7749,"spatialReference":{"wkid":4326}}}]}"""
                .Replace("NAME", name, StringComparison.Ordinal));
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        return Deserialize(body).AddResults.Should().ContainSingle(add => add.Success, body).Subject.ObjectId!.Value;
    }

    private async Task<string?> QueryNameAsync(string url)
    {
        using var response = await _fixture.Client.GetAsync(url);
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("features").EnumerateArray().Single()
            .GetProperty("attributes").GetProperty("name").GetString();
    }

    private async Task<int> QueryCountAsync(string url)
    {
        using var response = await _fixture.Client.GetAsync(url);
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt32();
    }

    private async Task<HttpResponseMessage> PostApplyEditsAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits", content);
    }

    private static ApplyEditsResponse Deserialize(string body)
        => JsonSerializer.Deserialize(body, FeatureServerJsonContext.Default.ApplyEditsResponse)
           ?? throw new InvalidOperationException($"Expected an apply-edits response: {body}");
}
