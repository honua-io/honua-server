// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.FeatureServer;

/// <summary>
/// Concurrency regression tests for <see cref="DistributedApplyEditsIdempotencyStore.TryReserveAsync"/>
/// (BH5-001): two concurrent applyEdits requests carrying the same Idempotency-Key must not both
/// proceed to execute the edit — only one reservation wins; the other returns false so the handler
/// can immediately 409 without executing.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.ApplyEdits)]
public sealed class ApplyEditsIdempotencyStoreTests
{
    private static ApplyEditsIdempotencyScope Scope(
        string key = "key-1",
        string service = "svc",
        int layer = 0,
        string principal = "alice")
        => new(service, layer, principal, key);

    // ─── TryReserveAsync concurrency ───────────────────────────────────────────

    /// <summary>
    /// BH5-001 regression: fire N concurrent TryReserveAsync calls for the same scope.
    /// The in-process fallback uses <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryAdd"/>
    /// which is natively atomic; exactly one caller must win the reservation.
    /// </summary>
    [UnitTest]
    public async Task TryReserveAsync_ConcurrentSameScope_ExactlyOneWins()
    {
        var store = new DistributedApplyEditsIdempotencyStore(
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        var scope = Scope();
        const int concurrency = 16;

        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => store.TryReserveAsync(scope))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r).Should().Be(1,
            "exactly one concurrent TryReserveAsync must win the reservation (BH5-001)");
        results.Count(r => !r).Should().Be(concurrency - 1,
            "all other concurrent attempts must lose the reservation and return false");
    }

    [UnitTest]
    public async Task TryReserveAsync_DifferentScopes_BothWin()
    {
        // Reservations for distinct scopes (different Idempotency-Keys) are independent;
        // both must succeed.
        var store = new DistributedApplyEditsIdempotencyStore(
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        var reservedA = await store.TryReserveAsync(Scope(key: "key-a"));
        var reservedB = await store.TryReserveAsync(Scope(key: "key-b"));

        reservedA.Should().BeTrue("scope A is a new key and must win the reservation");
        reservedB.Should().BeTrue("scope B uses a different key and must independently win");
    }

    [UnitTest]
    public async Task TryReserveAsync_AfterSetAsync_ReturnsFalse()
    {
        // After the response has been fully written via SetAsync, TryReserveAsync for the
        // same key must return false — the entry is occupied with the final response.
        var store = new DistributedApplyEditsIdempotencyStore(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        var scope = Scope();

        // First: reserve and complete the edit.
        var reserved = await store.TryReserveAsync(scope);
        reserved.Should().BeTrue("first reservation must be won");

        await store.SetAsync(scope, new Honua.Protocols.GeoServices.FeatureServer.Models.ApplyEditsResponse
        {
            Success = true,
            AddResults = [new Honua.Protocols.GeoServices.FeatureServer.Models.EditResult { ObjectId = 1, Success = true }]
        });

        // Second: a retry reservation attempt for the same key must fail because the response
        // is already stored (TryGetAsync would return the recorded response; TryReserveAsync
        // must not grant a second reservation to re-execute the edit).
        var reservedAgain = await store.TryReserveAsync(scope);
        reservedAgain.Should().BeFalse(
            "after the edit response is stored, TryReserveAsync must not grant a second execution");
    }

    // ─── TryReserveAsync / TryGetAsync interaction ─────────────────────────────

    [UnitTest]
    public async Task TryGetAsync_WhileReservationPending_ReturnsNull()
    {
        // A pending reservation (sentinel written but SetAsync not yet called) means the edit
        // is in-flight. TryGetAsync must return null (treat as "not yet recorded") so the
        // 409 path in the handler is not confused with a completed replay.
        var store = new DistributedApplyEditsIdempotencyStore(
            cache: null,
            NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

        var scope = Scope();

        await store.TryReserveAsync(scope);

        var result = await store.TryGetAsync(scope);

        result.Should().BeNull(
            "a pending sentinel must be invisible to TryGetAsync so the loser takes the 409 path");
    }
}