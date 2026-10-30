// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Caching;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// HTTP admission checks for the opt-in query response cache. The host disables the
/// development auth bypass so the caller principal stays empty, and opts into the
/// in-memory fallback because the test host has no Redis.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Query)]
public sealed class AnonymousResponseCacheAdmissionTests : IAsyncLifetime
{
    private const string SentinelJson = "{\"honuaCacheMarker\":true}";
    private static readonly byte[] SentinelPayload = Encoding.UTF8.GetBytes(SentinelJson);

    private readonly WebAppFixture _caching = CreateFixture(allowInMemoryFallback: true);
    private readonly WebAppFixture _fallbackRefused = CreateFixture(allowInMemoryFallback: false);

    public async Task InitializeAsync()
    {
        await _caching.InitializeAsync();
        await _fallbackRefused.InitializeAsync();

        // The default graph opens svc-test. FeatureServer resolution prefers the
        // protocol-specific service, so that service needs the same read policy.
        OpenAnonymousReads(_caching);
        OpenAnonymousReads(_fallbackRefused);
        AssertFallbackCacheMode(_caching, allowInMemoryFallback: true);
        AssertFallbackCacheMode(_fallbackRefused, allowInMemoryFallback: false);
    }

    public async Task DisposeAsync()
    {
        await _caching.DisposeAsync();
        await _fallbackRefused.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task EmptyPrincipal_StoresAndReplaysTheQueryResponse()
    {
        using var client = _caching.CreateClient();
        var path = QueryPath(resultRecordCount: 1);

        var before = QueryResponseKeys(_caching);
        var first = await GetSuccessAsync(client, path);
        first.Should().NotContain("honuaCacheMarker");
        var stored = RequireNewQueryKeys(_caching, before);

        ReplaceStoredPayload(stored);
        var replay = await ReadBodyAsync(client, path);
        replay.Should().Contain("honuaCacheMarker");
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task AdminSessionCookie_DoesNotReplayTheStoredResponse()
    {
        await CredentialDoesNotReplayAsync(
            resultRecordCount: 2,
            request => request.Headers.TryAddWithoutValidation("Cookie", "honua_admin_session=session-value"));
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task AdminLoginCookie_DoesNotReplayTheStoredResponse()
    {
        await CredentialDoesNotReplayAsync(
            resultRecordCount: 3,
            request => request.Headers.TryAddWithoutValidation("Cookie", "honua_admin_login=login-value"));
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task ForwardedClientCertificate_DoesNotReplayTheStoredResponse()
    {
        await CredentialDoesNotReplayAsync(
            resultRecordCount: 4,
            request => request.Headers.TryAddWithoutValidation("X-Forwarded-Client-Cert", "present"));
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task NonDefaultGdbVersion_BypassesTheStoredResponse()
    {
        using var client = _caching.CreateClient();
        var path = QueryPath(resultRecordCount: 5);
        var existing = QueryResponseKeys(_caching);
        await GetSuccessAsync(client, path);
        var stored = RequireNewQueryKeys(_caching, existing);
        ReplaceStoredPayload(stored);

        var before = QueryResponseKeys(_caching).Length;
        using var request = new HttpRequestMessage(HttpMethod.Get, path + "&gdbVersion=owner.branch");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("honuaCacheMarker");
        QueryResponseKeys(_caching).Length.Should().Be(before);
        (await ReadBodyAsync(client, path)).Should().Contain("honuaCacheMarker");
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task RepeatedOrderByFields_KeepArrivalOrderInTheCacheKey()
    {
        using var client = _caching.CreateClient();
        var forward = QueryPath(resultRecordCount: 6) + "&orderByFields=name&orderByFields=population%20DESC";
        var reverse = QueryPath(resultRecordCount: 6) + "&orderByFields=population%20DESC&orderByFields=name";

        var existing = QueryResponseKeys(_caching);
        await GetSuccessAsync(client, forward);
        var forwardKeys = RequireNewQueryKeys(_caching, existing);
        ReplaceStoredPayload(forwardKeys);

        (await GetSuccessAsync(client, reverse)).Should().NotContain("honuaCacheMarker");
        (await ReadBodyAsync(client, forward)).Should().Contain("honuaCacheMarker");
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task InMemoryFallbackWithoutOptIn_DoesNotStoreTheQueryResponse()
    {
        using var client = _fallbackRefused.CreateClient();
        var before = QueryResponseKeys(_fallbackRefused).Length;

        await GetSuccessAsync(client, QueryPath(resultRecordCount: 1));

        QueryResponseKeys(_fallbackRefused).Length.Should().Be(before);
    }

    private async Task CredentialDoesNotReplayAsync(int resultRecordCount, Action<HttpRequestMessage> configure)
    {
        using var client = _caching.CreateClient();
        var path = QueryPath(resultRecordCount);
        var existing = QueryResponseKeys(_caching);
        await GetSuccessAsync(client, path);
        var stored = RequireNewQueryKeys(_caching, existing);
        ReplaceStoredPayload(stored);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        configure(request);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("honuaCacheMarker");
        (await ReadBodyAsync(client, path)).Should().Contain("honuaCacheMarker");
    }

    private static void OpenAnonymousReads(WebAppFixture fixture)
        => fixture.UpdateV2ServiceMetadata(
            "test",
            accessPolicy: new AccessPolicy { AllowAnonymous = true });

    private static void AssertFallbackCacheMode(WebAppFixture fixture, bool allowInMemoryFallback)
    {
        var options = fixture.Services.GetRequiredService<IOptions<CacheOptions>>().Value;
        options.Enabled.Should().BeTrue();
        options.ResponseCachingEnabled.Should().BeTrue();
        options.ResponseCachingAllowInMemoryFallback.Should().Be(allowInMemoryFallback);
        fixture.Services.GetRequiredService<ICacheHealthChecker>().IsUsingFallback.Should().BeTrue();
    }

    private static string[] NewQueryKeys(WebAppFixture fixture, IEnumerable<string> existing)
        => QueryResponseKeys(fixture).Except(existing).ToArray();

    private static string[] RequireNewQueryKeys(WebAppFixture fixture, IEnumerable<string> existing)
    {
        var stored = NewQueryKeys(fixture, existing);
        if (stored.Length > 0)
        {
            return stored;
        }

        var cache = (RedisCacheService)fixture.Services.GetRequiredService<ICacheService>();
        var sample = string.Join(", ", ReadFallbackEntries(cache).Select(entry => entry.Key).Take(8));
        stored.Should().NotBeEmpty("fallback keys: {0}", sample.Length == 0 ? "(none)" : sample);
        return stored;
    }

    private static WebAppFixture CreateFixture(bool allowInMemoryFallback)
        => new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("Cache:Enabled", "true");
                builder.UseSetting("Cache:ResponseCachingEnabled", "true");
                if (allowInMemoryFallback)
                {
                    builder.UseSetting("Cache:ResponseCachingAllowInMemoryFallback", "true");
                }
            })
            .ConfigureServices(services =>
            {
                // The test host registers a process-local distributed cache. Drop it so the
                // cache service uses its in-memory fallback, which is what these tests cover.
                services.RemoveAll<IDistributedCache>();
            });

    private static string QueryPath(int resultRecordCount)
        => "/rest/services/test/FeatureServer/0/query?f=json&where=1%3D1&outFields=objectid&returnGeometry=false"
           + "&resultRecordCount=" + resultRecordCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> GetSuccessAsync(HttpClient client, string path)
    {
        var body = await ReadBodyAsync(client, path);
        var snippet = body.Length > 300 ? body[..300] : body;
        body.Should().Contain("\"features\"", "query body must be a feature payload: {0}", snippet);
        body.Should().NotContain("\"code\":499", "query body must be a feature payload: {0}", snippet);
        return body;
    }

    private static async Task<string> ReadBodyAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var snippet = body.Length > 300 ? body[..300] : body;
        response.IsSuccessStatusCode.Should().BeTrue("query returned {0}: {1}", (int)response.StatusCode, snippet);
        return body;
    }

    private static string[] QueryResponseKeys(WebAppFixture fixture)
    {
        var cache = (RedisCacheService)fixture.Services.GetRequiredService<ICacheService>();
        return ReadFallbackEntries(cache)
            .Select(entry => entry.Key)
            .Where(key => key.Contains("response:query:featureserver:", StringComparison.Ordinal))
            .ToArray();
    }

    private void ReplaceStoredPayload(IReadOnlyCollection<string> keys)
    {
        var cache = (RedisCacheService)_caching.Services.GetRequiredService<ICacheService>();
        var entries = ReadFallbackEntries(cache);
        var entryType = typeof(RedisCacheService).GetNestedType("CacheEntry", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Cache entry type is missing.");
        var dictionary = FallbackDictionary(cache);
        var indexer = dictionary.GetType().GetProperty("Item")
            ?? throw new InvalidOperationException("Fallback cache indexer is missing.");

        foreach (var key in keys)
        {
            var entry = entries.Single(candidate => candidate.Key == key);
            var cached = JsonSerializer.Deserialize(entry.Data, CacheJsonContext.Default.CachedResponse)
                ?? throw new InvalidOperationException("Cached response could not be read.");
            var marked = cached with { Payload = SentinelPayload };
            var serialized = JsonSerializer.SerializeToUtf8Bytes(marked, CacheJsonContext.Default.CachedResponse);
            var replacement = Activator.CreateInstance(entryType, serialized, entry.ExpiresAt);
            indexer.SetValue(dictionary, replacement, new object[] { key });
        }
    }

    private readonly record struct FallbackEntry(string Key, byte[] Data, DateTime ExpiresAt);

    private static object FallbackDictionary(RedisCacheService cache)
    {
        var field = typeof(RedisCacheService).GetField("_fallbackCache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Fallback cache field is missing.");
        return field.GetValue(cache) ?? throw new InvalidOperationException("Fallback cache is missing.");
    }

    private static List<FallbackEntry> ReadFallbackEntries(RedisCacheService cache)
    {
        var result = new List<FallbackEntry>();
        foreach (var item in (IEnumerable)FallbackDictionary(cache))
        {
            var itemType = item.GetType();
            var key = (string)itemType.GetProperty("Key")!.GetValue(item)!;
            var value = itemType.GetProperty("Value")!.GetValue(item)!;
            var data = (byte[])value.GetType().GetProperty("Data")!.GetValue(value)!;
            var expiresAt = (DateTime)value.GetType().GetProperty("ExpiresAt")!.GetValue(value)!;
            result.Add(new FallbackEntry(key, data, expiresAt));
        }

        return result;
    }
}
