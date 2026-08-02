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
        // Prior to the fix TryReserveAsync handed out a reservation unconditionally here.
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
        results.Count(static token => token is not null).Should().Be(1,
            "ConcurrentDictionary.TryAdd is the fallback for non-Redis IDistributedCache; " +
            "exactly one concurrent caller must win the reservation so only one edit executes");
        results.Count(static token => token is null).Should().Be(9,
            "all other concurrent callers must lose the reservation and return 409");
    }

    // ─── #3052: reservation release ──────────────────────────────────────────────

    /// <summary>
    /// #3052: a reservation that will never be replaced by a recorded response (the edit failed
    /// before dispatch, was rejected, rolled back, or committed no rows) must be releasable, so the
    /// client's retry wins the reservation again instead of losing it and getting a 409.
    /// </summary>
    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task ReleaseAsync_AfterReservation_AllowsTheSameKeyToReserveAgain()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);
        var scope = Scope(key: "release-then-retry");

        var token = await store.TryReserveAsync(scope);
        token.Should().NotBeNull();
        (await store.TryReserveAsync(scope)).Should().BeNull("the reservation is still held");

        await store.ReleaseAsync(scope, token!);

        (await store.TryReserveAsync(scope)).Should().NotBeNull(
            "a released reservation must not pin the key for the rest of the reservation window");
    }

    /// <summary>
    /// #3052: the in-process fallback path (no IDistributedCache configured) must release too.
    /// </summary>
    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task ReleaseAsync_WithInProcessFallback_AllowsTheSameKeyToReserveAgain()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);
        var scope = Scope(key: "release-then-retry-fallback");

        var token = await store.TryReserveAsync(scope);
        token.Should().NotBeNull();
        await store.ReleaseAsync(scope, token!);

        (await store.TryReserveAsync(scope)).Should().NotBeNull();
    }

    /// <summary>
    /// #3052 correctness guard, the direction that matters most: release is a compare-and-delete on
    /// the pending sentinel. Once a response has been recorded the key must stay reserved for the
    /// whole dedupe window, otherwise a late release would discard the replay value and let a
    /// duplicate retry re-apply an already-committed edit.
    /// </summary>
    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task ReleaseAsync_AfterRecordedResponse_LeavesTheReplayValueIntact()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);
        var scope = Scope(key: "recorded-then-released");

        var token = await store.TryReserveAsync(scope);
        token.Should().NotBeNull();
        await store.SetAsync(scope, SampleResponse(objectId: 1234));

        await store.ReleaseAsync(scope, token!);

        var replay = await store.TryGetAsync(scope);
        replay.Should().NotBeNull("a recorded response must survive a release");
        replay!.AddResults![0].ObjectId.Should().Be(1234);
    }

    /// <summary>
    /// #3052: releasing a key that was never reserved is a harmless no-op rather than a throw —
    /// the handler calls it from a finally block on already-failing paths.
    /// </summary>
    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task ReleaseAsync_WithoutReservation_IsANoOp()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);
        var scope = Scope(key: "never-reserved");

        await store.ReleaseAsync(scope, "a-token-that-never-reserved-anything");

        (await store.TryReserveAsync(scope)).Should().NotBeNull();
    }

    /// <summary>
    /// #3052 (Codex P1): a reservation carries a token unique to its owner, so a request whose
    /// reservation has already lapsed cannot delete the reservation that now belongs to someone
    /// else. Reproduces the lapse without waiting out the reservation window: the first owner
    /// releases (as the reservation window expiring would do), a retry re-reserves the key, and the
    /// first owner's late release must then be a no-op.
    ///
    /// Before the fix every reservation stored the identical pending sentinel, so the
    /// compare-and-delete matched the successor's reservation and freed it — after which a third
    /// same-key request could execute alongside the retry and duplicate its rows.
    /// </summary>
    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public async Task ReleaseAsync_WithLapsedOwnersToken_LeavesTheSuccessorsReservationHeld()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);
        var scope = Scope(key: "lapsed-owner");

        var lapsedToken = await store.TryReserveAsync(scope);
        lapsedToken.Should().NotBeNull();

        // The first owner's window lapses and a retry acquires the key for itself.
        await store.ReleaseAsync(scope, lapsedToken!);
        var successorToken = await store.TryReserveAsync(scope);
        successorToken.Should().NotBeNull();
        successorToken.Should().NotBe(lapsedToken, "each reservation must be uniquely identifiable");

        // The original request finally unwinds and releases the token it was handed.
        await store.ReleaseAsync(scope, lapsedToken!);

        (await store.TryReserveAsync(scope)).Should().BeNull(
            "the successor still owns the key, so a late release from the lapsed owner must not " +
            "free it and let a third request execute concurrently");
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
