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
    /// Returns <see langword="false"/> when the id was already seen or is empty.
    /// </summary>
    bool TryRemember(string id, DateTimeOffset notOnOrAfter);
}

/// <summary>
/// Distributed replay cache with a per-node memory fallback. Redis timeouts and connection
/// failures degrade to this node's memory rather than failing open or throwing into the ACS.
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

        if (_redis is { IsConnected: true })
        {
            try
            {
                var added = _redis.GetDatabase().StringSet(
                    key,
                    "1",
                    expiresAt - DateTimeOffset.UtcNow,
                    when: When.NotExists);
                if (!added)
                {
                    return false;
                }

                RememberInMemory(key, expiresAt);
                return true;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException && ex is not OperationCanceledException)
            {
                // This node still rejects a replay it has already seen.
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
