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

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
