// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Services;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// Behaviour of the default process-local content-hash artifact cache: round-trip
/// put/get, TTL expiry, and stable URIs.
/// </summary>
public class InMemoryContentHashArtifactCacheTests
{
    [Fact]
    public async Task Put_ThenTryGet_ReturnsReference()
    {
        var cache = new InMemoryContentHashArtifactCache();
        var payload = new SpecArtifactPayload
        {
            ContentHash = "hash1",
            Bytes = new byte[] { 1, 2, 3 }
        };

        var put = await cache.PutAsync(payload);
        var got = await cache.TryGetAsync("hash1");

        Assert.NotNull(got);
        Assert.Equal("hash1", got!.ContentHash);
        Assert.Equal(3, got.Bytes);
        Assert.Equal("/v1/spec/artifact/hash1", got.Uri);
        Assert.Equal(put.ProducedAt, got.ProducedAt);
    }

    [Fact]
    public async Task TryGet_UnknownHash_ReturnsNull()
    {
        var cache = new InMemoryContentHashArtifactCache();

        var got = await cache.TryGetAsync("missing");

        Assert.Null(got);
    }

    [Fact]
    public async Task OpenRead_ReturnsStoredBytes()
    {
        var cache = new InMemoryContentHashArtifactCache();
        var bytes = new byte[] { 42, 43, 44, 45 };
        await cache.PutAsync(new SpecArtifactPayload { ContentHash = "hash2", Bytes = bytes });

        using var stream = await cache.OpenReadAsync("hash2");
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    public async Task PutWithTtl_ExpiresAfterAdvance()
    {
        var clock = new TestTimeProvider();
        var cache = new InMemoryContentHashArtifactCache(clock);

        await cache.PutAsync(new SpecArtifactPayload
        {
            ContentHash = "ephemeral",
            Bytes = new byte[] { 1 },
            Ttl = TimeSpan.FromSeconds(30)
        });

        var beforeExpiry = await cache.TryGetAsync("ephemeral");
        Assert.NotNull(beforeExpiry);

        clock.Advance(TimeSpan.FromSeconds(31));

        var afterExpiry = await cache.TryGetAsync("ephemeral");
        Assert.Null(afterExpiry);
    }

    [Fact]
    public async Task Put_OverwritesEntryForSameHash()
    {
        var cache = new InMemoryContentHashArtifactCache();

        await cache.PutAsync(new SpecArtifactPayload { ContentHash = "h", Bytes = new byte[] { 1 } });
        await cache.PutAsync(new SpecArtifactPayload { ContentHash = "h", Bytes = new byte[] { 1, 2, 3, 4 } });

        var got = await cache.TryGetAsync("h");
        Assert.NotNull(got);
        Assert.Equal(4, got!.Bytes);
    }

    [Fact]
    public async Task PutAsync_SweepsPreviouslyExpiredEntries()
    {
        // Regression for the review finding: TTL-backed entries used to leak
        // memory because expiration was only reclaimed lazily inside
        // TryGetAsync/OpenReadAsync. A workload that keeps producing unique
        // mutable-source hashes therefore left expired byte arrays resident
        // indefinitely. PutAsync now opportunistically sweeps TTL-expired
        // entries so writers reclaim memory without requiring a read.
        var clock = new TestTimeProvider();
        var cache = new InMemoryContentHashArtifactCache(clock);

        await cache.PutAsync(new SpecArtifactPayload
        {
            ContentHash = "ephemeral",
            Bytes = new byte[] { 1 },
            Ttl = TimeSpan.FromSeconds(30)
        });

        clock.Advance(TimeSpan.FromSeconds(31));

        // A subsequent Put on a completely different key must reclaim the
        // already-expired entry without requiring a read of the old hash.
        await cache.PutAsync(new SpecArtifactPayload
        {
            ContentHash = "fresh",
            Bytes = new byte[] { 2 }
        });

        // The fresh entry is readable, and the expired entry is gone from the
        // underlying store — a follow-on TryGet returns null and crucially does
        // not need to perform its own sweep, proving the write-time reclaim.
        var fresh = await cache.TryGetAsync("fresh");
        Assert.NotNull(fresh);

        var expired = await cache.TryGetAsync("ephemeral");
        Assert.Null(expired);
    }

    [Fact]
    public async Task PutAsync_PreservesNonExpiredTtlEntries()
    {
        // Guardrail: the sweep must only reclaim entries whose TTL has elapsed.
        // Entries that still have life remaining — including the one we just
        // wrote — survive unchanged.
        var clock = new TestTimeProvider();
        var cache = new InMemoryContentHashArtifactCache(clock);

        await cache.PutAsync(new SpecArtifactPayload
        {
            ContentHash = "still-alive",
            Bytes = new byte[] { 1 },
            Ttl = TimeSpan.FromMinutes(5)
        });

        clock.Advance(TimeSpan.FromMinutes(1));

        await cache.PutAsync(new SpecArtifactPayload
        {
            ContentHash = "fresh",
            Bytes = new byte[] { 2 }
        });

        Assert.NotNull(await cache.TryGetAsync("still-alive"));
        Assert.NotNull(await cache.TryGetAsync("fresh"));
    }

    [Fact]
    public async Task PutAsync_ExceedsMaxEntries_EvictsDownToBudget()
    {
        // Regression for the review finding: EnforceBudget's over-budget eviction
        // loop used to re-sum every resident entry's byte length on every single
        // eviction step (O(n^2) for a heavily over-budget cache). It now tracks a
        // running total incrementally, so this also exercises that bookkeeping
        // stays correct across many distinct-key inserts.
        var cache = new InMemoryContentHashArtifactCache();
        const int overBudgetBy = 64;

        for (var i = 0; i < InMemoryContentHashArtifactCache.MaxEntries + overBudgetBy; i++)
        {
            await cache.PutAsync(new SpecArtifactPayload
            {
                ContentHash = $"entry-{i}",
                Bytes = new byte[] { 1 }
            });
        }

        var survivingCount = 0;
        for (var i = 0; i < InMemoryContentHashArtifactCache.MaxEntries + overBudgetBy; i++)
        {
            if (await cache.TryGetAsync($"entry-{i}") is not null)
            {
                survivingCount++;
            }
        }

        Assert.True(
            survivingCount <= InMemoryContentHashArtifactCache.MaxEntries,
            $"expected at most {InMemoryContentHashArtifactCache.MaxEntries} surviving entries, found {survivingCount}");
    }

    [Fact]
    public async Task PutAsync_OverwriteThenEvict_RunningByteTotalStaysConsistent()
    {
        // Overwriting a key must replace its contribution to the running byte
        // total (not add to it) before eviction accounting runs, otherwise the
        // budget check would drift and either over-evict or never evict.
        var cache = new InMemoryContentHashArtifactCache();

        await cache.PutAsync(new SpecArtifactPayload { ContentHash = "grows", Bytes = new byte[16] });
        await cache.PutAsync(new SpecArtifactPayload { ContentHash = "grows", Bytes = new byte[4096] });

        var got = await cache.TryGetAsync("grows");
        Assert.NotNull(got);
        Assert.Equal(4096, got!.Bytes);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
