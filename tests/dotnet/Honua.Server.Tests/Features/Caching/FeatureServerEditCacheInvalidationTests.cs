// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Caching;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Read-your-writes across the exact response cache, end to end: a real HTTP <c>applyEdits</c>
/// must invalidate a FeatureServer query response another replica has cached, immediately rather
/// than at the end of the namespace TTL (honua-server#4406, the missing test asset behind #4259).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CacheServiceResponseCacheReplicaTests"/> already proves the second half of the chain —
/// that once a pattern is invalidated, a warm replica stops returning the old response — using two
/// <see cref="CacheServiceResponseCache"/> instances over one shared cache. What nothing covered is
/// the first half: that an <em>edit</em> actually reaches that invalidation with a pattern matching
/// the layer's cached query keys. Greps for <c>read-after-write</c> / <c>ReadAfterWrite</c> /
/// <c>readYourWrite</c> returned no test anywhere in the repository.
/// </para>
/// <para>
/// The test host authenticates every request through the dev-auth bypass and
/// <c>ResponseCacheUtilities.ShouldCache</c> skips authenticated requests, so the endpoint cannot
/// populate the cache for itself here. The replica below therefore seeds the cache directly, under
/// the key shape the FeatureServer query path uses, over the <see cref="ICacheService"/> the running
/// host resolves — which is the same shared cache the edit's invalidation writes to. That models a
/// second server instance that served an anonymous query and cached it, which is exactly the #4259
/// scenario.
/// </para>
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.ApplyEdits)]
public sealed class FeatureServerEditCacheInvalidationTests : IAsyncLifetime
{
    private const string ServiceId = "test";
    private const int LayerId = 0;

    /// <summary>The key shape <c>ResponseCacheUtilities.BuildFeatureServerKey</c> produces.</summary>
    private const string CachedQueryKey = "query:featureserver:service:test:layer:0:query-hash";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("Cache:Enabled", "true");
            builder.UseSetting("Cache:ResponseCachingEnabled", "true");
        });

    private CacheServiceResponseCache _warmReplica = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(ServiceId, ["Create", "Update", "Delete"]);
        _warmReplica = new CacheServiceResponseCache(_fixture.Services.GetRequiredService<ICacheService>());
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_Add_InvalidatesTheLayersCachedQueryResponsesOnAWarmReplica()
    {
        await SeedWarmReplicaAsync();

        await ApplyEditsAsync(
            """{"adds":[{"attributes":{"name":"cache-add"},"geometry":{"x":-122.4194,"y":37.7749,"spatialReference":{"wkid":4326}}}]}""");

        (await _warmReplica.GetAsync<string>(CachedQueryKey)).Should().BeNull(
            "an add must invalidate the layer's cached query responses immediately — read-your-writes " +
            "cannot be deferred to the end of the 30-second namespace TTL (#4259)");
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_Update_InvalidatesTheLayersCachedQueryResponsesOnAWarmReplica()
    {
        var objectId = await AddFeatureAsync("cache-update-before");
        await SeedWarmReplicaAsync();

        await ApplyEditsAsync(
            """{"updates":[{"attributes":{"objectid":OBJECTID,"name":"cache-update-after"}}]}"""
                .Replace("OBJECTID", objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

        (await _warmReplica.GetAsync<string>(CachedQueryKey)).Should().BeNull(
            "an update must invalidate the layer's cached query responses");
        (await _fixture.ReadStoredFeatureNameAsync(LayerId, objectId)).Should().Be(
            "cache-update-after",
            "the edit itself must have committed — an invalidation without a write proves nothing");
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_Delete_InvalidatesTheLayersCachedQueryResponsesOnAWarmReplica()
    {
        var objectId = await AddFeatureAsync("cache-delete");
        await SeedWarmReplicaAsync();

        await ApplyEditsAsync(
            """{"deletes":[OBJECTID]}"""
                .Replace("OBJECTID", objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

        (await _warmReplica.GetAsync<string>(CachedQueryKey)).Should().BeNull(
            "a delete must invalidate the layer's cached query responses");
        (await _fixture.ReadStoredFeatureNameAsync(LayerId, objectId)).Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_AfterInvalidation_AReplicaCachesAndServesTheNewGeneration()
    {
        // The complementary half: invalidation must not poison the namespace. After the edit, the
        // replica has to be able to cache and read back a fresh response.
        await SeedWarmReplicaAsync();
        await ApplyEditsAsync(
            """{"adds":[{"attributes":{"name":"cache-regeneration"},"geometry":{"x":-122.4,"y":37.7,"spatialReference":{"wkid":4326}}}]}""");
        (await _warmReplica.GetAsync<string>(CachedQueryKey)).Should().BeNull();

        await _warmReplica.SetAsync(CachedQueryKey, "after-edit", TimeSpan.FromMinutes(5));

        (await _warmReplica.GetAsync<string>(CachedQueryKey)).Should().Be("after-edit");
        var otherReplica = new CacheServiceResponseCache(_fixture.Services.GetRequiredService<ICacheService>());
        (await otherReplica.GetAsync<string>(CachedQueryKey)).Should().Be(
            "after-edit",
            "the regenerated response must be visible to every replica, not only the one that wrote it");
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_FailedEdit_LeavesTheCacheAlone()
    {
        // The negative control: an edit that changed nothing must not evict, or every rejected
        // request would become a cache-stampede trigger.
        await SeedWarmReplicaAsync();

        var response = await ApplyEditsRawAsync("""{"updates":[{"attributes":{"objectid":999999,"name":"ghost"}}]}""");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"success\":false", body);

        (await _warmReplica.GetAsync<string>(CachedQueryKey)).Should().Be(
            "before-edit",
            "an edit that committed no rows must not invalidate the layer's cached responses");
    }

    private async Task SeedWarmReplicaAsync()
    {
        await _warmReplica.SetAsync(CachedQueryKey, "before-edit", TimeSpan.FromMinutes(5));
        (await _warmReplica.GetAsync<string>(CachedQueryKey)).Should().Be(
            "before-edit",
            "the replica must actually be warm before the edit, or the assertion below is vacuous");
    }

    private async Task<long> AddFeatureAsync(string name)
    {
        var body = await ApplyEditsAsync(
            """{"adds":[{"attributes":{"name":"NAME"},"geometry":{"x":-122.4194,"y":37.7749,"spatialReference":{"wkid":4326}}}]}"""
                .Replace("NAME", name, StringComparison.Ordinal));
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();
    }

    private async Task<string> ApplyEditsAsync(string json)
    {
        var response = await ApplyEditsRawAsync(json);
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        body.Should().Contain("\"success\":true", body);
        return body;
    }

    private async Task<HttpResponseMessage> ApplyEditsRawAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits", content);
    }
}
