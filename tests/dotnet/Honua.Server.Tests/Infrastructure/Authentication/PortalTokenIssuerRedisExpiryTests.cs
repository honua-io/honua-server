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
    [IntegrationTest]
    public async Task ValidateAsync_RedisAnswers_TokenValidUntilAdvertisedExpiryAndInvalidFromIt()
    {
        using var cache = new RedisCache(Options.Create(new RedisCacheOptions
        {
            Configuration = redis.ConnectionString,
            InstanceName = $"portal-expiry-{Guid.NewGuid():N}:",
        }));
        var issuer = new PortalTokenIssuer(new MemoryCache(new MemoryCacheOptions()), NullLogger<PortalTokenIssuer>.Instance, cache);
        var binding = new PortalTokenBinding(Referer: null, ClientIp: "10.0.0.7");

        // A fractional lifetime is what exposed the defect: Redis keeps key lifetimes in whole
        // seconds, so a 5.9 s lifetime backed by a 5 s key vanished 900 ms before the expiry the
        // client was given. Probing between the truncated key lifetime and ExpiresAt separates
        // the two.
        var expiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(5900);
        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest("alice", null, "tenant-A", ["viewer"], PortalTokenClientType.Ip, "10.0.0.7", expiresAt),
            CancellationToken.None);

        // Another replica has no memory entry, so only the distributed tier can answer it.
        var replica = new PortalTokenIssuer(new MemoryCache(new MemoryCacheOptions()), NullLogger<PortalTokenIssuer>.Instance, cache);
        await Task.Delay(expiresAt - TimeSpan.FromMilliseconds(500) - DateTimeOffset.UtcNow);
        var beforeExpiry = await replica.ValidateAsync(issuance.Token, binding, CancellationToken.None);
        var probedAt = DateTimeOffset.UtcNow;

        probedAt.Should().BeBefore(expiresAt, "the pre-expiry probe must finish inside the advertised lifetime to mean anything");
        beforeExpiry.Should().NotBeNull("a token must keep validating until the expiry the server advertised");
        beforeExpiry!.ExpiresAt.Should().Be(expiresAt);

        // Task.Delay truncates to whole milliseconds and can wake just before the instant, so
        // wait on the clock the issuer itself compares against.
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, (expiresAt - DateTimeOffset.UtcNow).TotalMilliseconds)));
        }

        (await replica.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().BeNull("the token stops validating at its advertised expiry");
        (await issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().BeNull("the issuing replica's memory tier must not outlive the advertised expiry either");
    }
}
