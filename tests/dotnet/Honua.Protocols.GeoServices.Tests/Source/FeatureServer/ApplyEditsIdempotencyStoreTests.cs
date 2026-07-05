// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Unit coverage for the server-side at-most-once applyEdits store and Idempotency-Key header parsing
/// (#2250): a recorded response is replayed for a repeated key, keys are isolated by principal/service/
/// layer, and a malformed header is rejected.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
public sealed class ApplyEditsIdempotencyStoreTests
{
    private static ApplyEditsResponse SampleResponse(long objectId = 42)
        => new()
        {
            Success = true,
            AddResults = [new EditResult { ObjectId = objectId, Success = true }]
        };

    private static ApplyEditsIdempotencyScope Scope(
        string key = "key-1",
        string service = "svc",
        int layer = 0,
        string principal = "alice")
        => new(service, layer, principal, key);

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task TryGetAsync_BeforeSet_ReturnsNull()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        var result = await store.TryGetAsync(Scope());

        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task SetThenTryGet_WithDistributedCache_ReplaysOriginalObjectId()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        await store.SetAsync(Scope(), SampleResponse(objectId: 99));

        var replay = await store.TryGetAsync(Scope());

        replay.Should().NotBeNull();
        replay!.Success.Should().BeTrue();
        replay.AddResults.Should().ContainSingle();
        replay.AddResults![0].ObjectId.Should().Be(99);
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task SetThenTryGet_WithInProcessFallback_ReplaysOriginalObjectId()
    {
        // No IDistributedCache configured -> the in-process fallback dictionary is exercised.
        var store = new DistributedApplyEditsIdempotencyStore(
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        await store.SetAsync(Scope(), SampleResponse(objectId: 7));

        var replay = await store.TryGetAsync(Scope());

        replay.Should().NotBeNull();
        replay!.AddResults![0].ObjectId.Should().Be(7);
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task TryGetAsync_DifferentPrincipal_DoesNotReplay()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        await store.SetAsync(Scope(principal: "alice"), SampleResponse());

        var replay = await store.TryGetAsync(Scope(principal: "mallory"));

        replay.Should().BeNull("an idempotency key must not replay another principal's response");
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task TryGetAsync_DifferentLayer_DoesNotReplay()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        await store.SetAsync(Scope(layer: 0), SampleResponse());

        var replay = await store.TryGetAsync(Scope(layer: 1));

        replay.Should().BeNull("the same key on a different layer is a distinct edit");
    }

    // ─── BH7-002 regression ──────────────────────────────────────────────────────

    /// <summary>
    /// BH7-002: concurrent TryReserveAsync on a non-Redis IDistributedCache (e.g.
    /// MemoryDistributedCache — the default deployment) must still produce exactly one
    /// winner per key.  The store must fall through to the in-process ConcurrentDictionary
    /// path instead of returning true for every caller (the previous "fall-open" bug).
    /// </summary>
    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task TryReserveAsync_ConcurrentSameScope_NonRedisCache_ExactlyOneWins()
    {
        // Arrange: MemoryDistributedCache configured but no IConnectionMultiplexer.
        // Prior to the fix TryReserveAsync returned true unconditionally here.
        var store = new DistributedApplyEditsIdempotencyStore(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        var scope = Scope(key: "bh7-002-race");

        // Act: 10 concurrent reservations for the same scope.
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => store.TryReserveAsync(scope))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert: exactly one winner (BH7-002).
        results.Count(static r => r).Should().Be(1,
            "ConcurrentDictionary.TryAdd is the fallback for non-Redis IDistributedCache; " +
            "exactly one concurrent caller must win the reservation so only one edit executes");
        results.Count(static r => !r).Should().Be(9,
            "all other concurrent callers must lose the reservation and return 409");
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void TryResolveKey_NoHeader_ReturnsNullKeyWithoutError()
    {
        var context = new DefaultHttpContext();

        var ok = ApplyEditsIdempotency.TryResolveKey(context, out var key, out var error);

        ok.Should().BeTrue();
        key.Should().BeNull();
        error.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void TryResolveKey_ValidHeader_ReturnsTrimmedKey()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApplyEditsIdempotency.HeaderName] = "  abc-123  ";

        var ok = ApplyEditsIdempotency.TryResolveKey(context, out var key, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        key.Should().Be("abc-123");
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void TryResolveKey_EmptyHeader_ReturnsError()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApplyEditsIdempotency.HeaderName] = "   ";

        var ok = ApplyEditsIdempotency.TryResolveKey(context, out var key, out var error);

        ok.Should().BeFalse();
        key.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void TryResolveKey_TooLongHeader_ReturnsError()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApplyEditsIdempotency.HeaderName] =
            new string('x', ApplyEditsIdempotency.MaxKeyLength + 1);

        var ok = ApplyEditsIdempotency.TryResolveKey(context, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void TryResolveKey_ControlCharacter_ReturnsError()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApplyEditsIdempotency.HeaderName] = "abcdef";

        var ok = ApplyEditsIdempotency.TryResolveKey(context, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }
}
