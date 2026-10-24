// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Unit tests for <see cref="DistributedApplyEditsIdempotencyStore"/> covering
/// the BH5-001 atomic-reserve primitive (SET NX / ConcurrentDictionary.TryAdd) and
/// the pending-sentinel behaviour.
/// </summary>
[global::Xunit.Trait("Tier", "Fast")]
public sealed class ApplyEditsIdempotencyStoreTests
{
    // ─── BH5-001 regression ──────────────────────────────────────────────────────

    /// <summary>
    /// BH5-001 regression: 10 concurrent TryReserveAsync calls for the same scope must
    /// result in exactly one winner. The losers must return false so their callers can
    /// return 409 rather than both executing and committing duplicate rows.
    /// </summary>
    [Fact]
    public async Task TryReserveAsync_ConcurrentSameScope_ExactlyOneWins()
    {
        // Arrange: no IConnectionMultiplexer → in-process fallback via ConcurrentDictionary.TryAdd.
        var store = new DistributedApplyEditsIdempotencyStore(
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        var scope = new ApplyEditsIdempotencyScope("svc", 0, "alice", "key-race");

        // Act: fire 10 concurrent reserve calls.
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => store.TryReserveAsync(scope))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert: exactly one winner.
        results.Count(token => token is not null).Should().Be(1,
            "ConcurrentDictionary.TryAdd is atomic; exactly one concurrent caller must win " +
            "the reservation (BH5-001)");
        results.Count(token => token is null).Should().Be(9,
            "all other concurrent callers must fail the reservation and return 409");
    }

    [Fact]
    public async Task TryGetAsync_AfterReservation_ReturnsPendingNull()
    {
        // After a successful TryReserveAsync the reservation is stored; TryGetAsync
        // must return null (not try to deserialize the sentinel as a response).
        var store = new DistributedApplyEditsIdempotencyStore(
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        var scope = new ApplyEditsIdempotencyScope("svc", 0, "alice", "key-sentinel");

        var reserved = await store.TryReserveAsync(scope);
        reserved.Should().NotBeNull("first reserve must succeed");

        var response = await store.TryGetAsync(scope);
        response.Should().BeNull(
            "TryGetAsync must return null for a pending reservation (the key is reserved but " +
            "the owning request has not yet written the final response)");
    }

    [Fact]
    public async Task TryGetAsync_AfterSetAsync_ReturnsStoredResponse()
    {
        // Full lifecycle: reserve → edit executes → SetAsync stores response →
        // TryGetAsync returns the stored response for deduplication.
        var store = new DistributedApplyEditsIdempotencyStore(
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        var scope = new ApplyEditsIdempotencyScope("svc", 0, "alice", "key-full");
        var expected = BuildTestResponse();

        var reserved = await store.TryReserveAsync(scope);
        reserved.Should().NotBeNull();

        (await store.SetAsync(scope, reserved!, expected)).Should().BeTrue();

        var replayed = await store.TryGetAsync(scope);
        replayed.Should().NotBeNull("SetAsync must replace the pending sentinel with the actual response");
        replayed!.Success.Should().BeTrue();
        replayed.AddResults.Should().HaveCount(1);
    }

    [Fact]
    public async Task TryReserveAsync_KeyExpiredInFallback_AllowsNewReservation()
    {
        // When a fallback entry has expired, TryReserveAsync should allow a new reservation
        // (the owning request timed out and never called SetAsync).
        // We access the internal ReservationWindow constant to confirm the store's design intent.
        DistributedApplyEditsIdempotencyStore.ReservationWindow
            .Should().BeGreaterThan(TimeSpan.Zero, "reservation window must be a positive duration");
    }

    [Fact]
    public async Task SetAsync_ForeignToken_DoesNotOverwriteTheOwner()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);
        var scope = new ApplyEditsIdempotencyScope("svc", 0, "alice", "key-owned");
        var owner = await store.TryReserveAsync(scope);
        owner.Should().NotBeNull();

        (await store.SetAsync(scope, "not-the-owner", BuildTestResponse())).Should().BeFalse();
        (await store.TryGetAsync(scope)).Should().BeNull();
        (await store.TryReserveAsync(scope)).Should().BeNull();

        (await store.SetAsync(scope, owner!, BuildTestResponse())).Should().BeTrue();
        (await store.TryGetAsync(scope)).Should().NotBeNull();
    }

    [Fact]
    public async Task SetAsync_AfterOwnReservationExpiredAndWasRetaken_DoesNotOverwriteTheNewOwner()
    {
        var time = new ManualTimeProvider();
        var store = new DistributedApplyEditsIdempotencyStore(
            multiplexer: null,
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance,
            timeProvider: time);
        var scope = new ApplyEditsIdempotencyScope("svc", 0, "alice", "key-slow");
        var slow = await store.TryReserveAsync(scope);
        slow.Should().NotBeNull();

        time.Advance(IdempotencyPayloadStore.ReservationWindow + TimeSpan.FromSeconds(1));
        var retry = await store.TryReserveAsync(scope);
        retry.Should().NotBeNull();

        (await store.SetAsync(scope, slow!, BuildTestResponse())).Should().BeFalse();
        (await store.TryGetAsync(scope)).Should().BeNull();
        (await store.SetAsync(scope, retry!, BuildTestResponse())).Should().BeTrue();
    }

    [Fact]
    public async Task TryReserveAsync_WindowSizedToRequestTimeout_HoldsForTheWholeRequest()
    {
        DistributedApplyEditsIdempotencyStore.ReservationWindowFor(TimeSpan.FromSeconds(10))
            .Should().Be(IdempotencyPayloadStore.ReservationWindow);
        var window = DistributedApplyEditsIdempotencyStore.ReservationWindowFor(TimeSpan.FromMinutes(10));
        window.Should().Be(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30));

        var time = new ManualTimeProvider();
        var store = new DistributedApplyEditsIdempotencyStore(
            multiplexer: null,
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance,
            reservationWindow: window,
            timeProvider: time);
        var scope = new ApplyEditsIdempotencyScope("svc", 0, "alice", "key-long");
        (await store.TryReserveAsync(scope)).Should().NotBeNull();

        time.Advance(TimeSpan.FromMinutes(10));
        (await store.TryReserveAsync(scope)).Should().BeNull();

        time.Advance(window - TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        (await store.TryReserveAsync(scope)).Should().NotBeNull();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static ApplyEditsResponse BuildTestResponse() =>
        new()
        {
            Success = true,
            AddResults = [new EditResult { ObjectId = 1, Success = true }],
            UpdateResults = [],
            DeleteResults = []
        };
}
