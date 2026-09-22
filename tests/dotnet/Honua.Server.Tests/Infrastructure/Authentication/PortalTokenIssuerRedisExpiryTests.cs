// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Proves a portal token honours its advertised expiry when a real Redis distributed cache
/// answers validation (honua-server#4777).
/// </summary>
[Collection(RedisFixture.CollectionName)]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security, Operations.SecurityTesting)]
public sealed class PortalTokenIssuerRedisExpiryTests(RedisFixture redis)
{
    private const int Attempts = 3;

    [IntegrationTest]
    public async Task ValidateAsync_RedisAnswers_TokenValidUntilAdvertisedExpiryAndInvalidFromIt()
    {
        using var cache = new RedisCache(Options.Create(new RedisCacheOptions
        {
            Configuration = redis.ConnectionString,
            InstanceName = $"portal-expiry-{Guid.NewGuid():N}:",
        }));
        var issuer = new PortalTokenIssuer(new MemoryCache(new MemoryCacheOptions()), NullLogger<PortalTokenIssuer>.Instance, cache);
        // Another replica has no memory entry, so only the distributed tier can answer it.
        var replica = new PortalTokenIssuer(new MemoryCache(new MemoryCacheOptions()), NullLogger<PortalTokenIssuer>.Instance, cache);
        var binding = new PortalTokenBinding(Referer: null, ClientIp: "10.0.0.7");

        // Connect first, so issuance below measures writing the entry rather than opening Redis.
        await cache.GetAsync("portal-expiry-warm-up", CancellationToken.None);

        // Redis keeps key lifetimes in whole seconds and rounds down, so a 5.95 s lifetime used to
        // be backed by a 5 s key and the token vanished 950 ms before the expiry the client was
        // given. The probe runs inside that last partial second. A probe the scheduler delays past
        // ExpiresAt shows nothing either way, so that attempt is repeated instead of judged.
        var lifetime = TimeSpan.FromMilliseconds(5950);
        var probeLead = TimeSpan.FromMilliseconds(850);
        for (var attempt = 1; ; attempt++)
        {
            var expiresAt = DateTimeOffset.UtcNow + lifetime;
            var issuance = await issuer.IssueAsync(
                new PortalTokenIssueRequest("alice", null, "tenant-A", ["viewer"], PortalTokenClientType.Ip, "10.0.0.7", expiresAt),
                CancellationToken.None);

            await DelayUntilAsync(expiresAt - probeLead);
            var beforeExpiry = await replica.ValidateAsync(issuance.Token, binding, CancellationToken.None);
            if (DateTimeOffset.UtcNow >= expiresAt)
            {
                attempt.Should().BeLessThan(Attempts, "the pre-expiry probe never completed inside the advertised lifetime");
                continue;
            }

            beforeExpiry.Should().NotBeNull("a token must keep validating until the expiry the server advertised");
            beforeExpiry!.ExpiresAt.Should().Be(expiresAt);

            await DelayUntilAsync(expiresAt);
            (await replica.ValidateAsync(issuance.Token, binding, CancellationToken.None))
                .Should().BeNull("the token stops validating at its advertised expiry");
            (await issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
                .Should().BeNull("the issuing replica's memory tier must not outlive the advertised expiry either");
            return;
        }
    }

    // Task.Delay truncates to whole milliseconds and can wake just before the instant, so wait on
    // the clock the issuer compares against. An instant that has already passed returns at once.
    private static async Task DelayUntilAsync(DateTimeOffset instant)
    {
        while (DateTimeOffset.UtcNow < instant)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, (instant - DateTimeOffset.UtcNow).TotalMilliseconds)));
        }
    }
}
