// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Honua.Server.Features.Identity.Saml;

/// <summary>
/// One-time cache of SAML assertion and response identifiers. A repeated id is a replay.
/// </summary>
internal interface ISamlAssertionReplayCache
{
    /// <summary>
    /// Records <paramref name="id"/> until <paramref name="notOnOrAfter"/>.
    /// Returns <see langword="false"/> when the id was already seen, is empty, or a configured
    /// Redis store cannot record the claim. Node-local memory is not a substitute for Redis.
    /// </summary>
    bool TryRemember(string id, DateTimeOffset notOnOrAfter);
}

/// <summary>
/// Distributed replay cache. When Redis is configured, the assertion is accepted only after
/// this node records the SET NX. A disconnect or timeout rejects the assertion. Memory is the
/// store only when no Redis multiplexer is configured.
/// </summary>
internal sealed class SamlAssertionReplayCache(IMemoryCache memoryCache, IConnectionMultiplexer? redis = null)
    : ISamlAssertionReplayCache
{
    private const string KeyPrefix = "saml:replay:";

    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly IConnectionMultiplexer? _redis = redis;

    /// <inheritdoc />
    public bool TryRemember(string id, DateTimeOffset notOnOrAfter)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var expiresAt = notOnOrAfter > DateTimeOffset.UtcNow
            ? notOnOrAfter
            : DateTimeOffset.UtcNow.AddMinutes(5);
        var key = KeyPrefix + id.Trim();

        if (_redis is not null)
        {
            // A configured Redis store is the only authority for this claim. Accepting from
            // this node's memory would let another node replay the same assertion.
            if (!_redis.IsConnected)
            {
                return false;
            }

            try
            {
                return _redis.GetDatabase().StringSet(
                    key,
                    "1",
                    expiresAt - DateTimeOffset.UtcNow,
                    when: When.NotExists);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException && ex is not OperationCanceledException)
            {
                return false;
            }
        }

        return RememberInMemory(key, expiresAt);
    }

    private bool RememberInMemory(string key, DateTimeOffset expiresAt)
    {
        var created = false;
        _memoryCache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpiration = expiresAt;
            created = true;
            return (byte)1;
        });
        return created;
    }
}
