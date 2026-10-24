// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Honua.Ai.Protocols.Mcp;

internal sealed partial class RedisMcpSessionStore
{
    // Existing session keys remain unchanged. These request keys explicitly hash
    // to that untagged session key's slot, so the authority check and transition
    // are atomic on both standalone Redis and Redis Cluster.
    internal static RedisKey[] RequestKeys(string sessionId) =>
        [Key(sessionId), $"mcp:requests:{{{Key(sessionId)}}}", $"mcp:requests:expiry:{{{Key(sessionId)}}}"];

    internal static RedisKey PrincipalRequestKey(string principalKey) =>
        "mcp:requests:principal:" + PrincipalHash(principalKey);

    private static string PrincipalHash(string principalKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(principalKey)));

    // The cross-session principal quota is a separate, single-slot reservation.
    // Each member includes its generation; compensation can never free a later
    // request using the same id. Its fixed deadline also bounds admission ghosts.
    private const string ReservePrincipalScript = """
        local clock = redis.call('TIME')
        local now = tonumber(clock[1])*1000 + math.floor(tonumber(clock[2])/1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) then return 0 end
        local expires = now + tonumber(ARGV[3])
        redis.call('ZADD', KEYS[1], expires, ARGV[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        return expires
        """;

    // Hash values retain the generation, cancellation bit and principal hash.
    // Finish checks entry ownership BEFORE mutation, even after session expiry.
    // Reads, cancellations and completion never renew session/request deadlines.
    private const string RequestScript = """
        local clock = redis.call('TIME')
        local now = tonumber(clock[1])*1000 + math.floor(tonumber(clock[2])/1000)
        local field, generation, operation = ARGV[1], ARGV[2], ARGV[3]
        local expired = redis.call('ZRANGEBYSCORE', KEYS[3], '-inf', now)
        for _, item in ipairs(expired) do redis.call('HDEL', KEYS[2], item) end
        redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', now)
        local value = redis.call('HGET', KEYS[2], field)
        local session = redis.call('GET', KEYS[1])
        if session and cjson.decode(session).principalKey ~= ARGV[4] then return 0 end
        if value and string.sub(value, 36) ~= ARGV[5] then return 0 end
        if operation == 'Finish' then
            if not value or string.sub(value, 1, 32) ~= generation then return 0 end
            redis.call('HDEL', KEYS[2], field)
            redis.call('ZREM', KEYS[3], field)
            if not session then return 0 end
            if string.sub(value, 34, 34) == '1' then return 2 else return 1 end
        end
        if not session then return 0 end
        if operation == 'Register' then
            if value then return 3 end
            if redis.call('ZCARD', KEYS[3]) >= tonumber(ARGV[6]) then return 4 end
            local expires = tonumber(ARGV[7])
            if expires <= now then return 0 end
            redis.call('HSET', KEYS[2], field, generation .. ':0:' .. ARGV[5])
            redis.call('ZADD', KEYS[3], expires, field)
            redis.call('PEXPIRE', KEYS[2], ARGV[8])
            redis.call('PEXPIRE', KEYS[3], ARGV[8])
            return 1
        end
        if not value then return 0 end
        if operation == 'Cancel' then
            redis.call('HSET', KEYS[2], field, string.sub(value, 1, 32) .. ':1:' .. ARGV[5])
            return 2
        end
        if string.sub(value, 1, 32) ~= generation then return 0 end
        if string.sub(value, 34, 34) == '1' then return 2 else return 1 end
        """;

    public async Task<McpRequestState> UpdateRequestAsync(
        string sessionId, string principalKey, string requestKey, string generation,
        McpRequestOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Canonical shared-session ids are random lowercase hex. Refuse a
        // noncanonical brace-bearing id instead of changing its cluster routing.
        if (sessionId.Contains('{') || sessionId.Contains('}'))
        {
            return McpRequestState.Missing;
        }

        var database = redis.GetDatabase();
        var principalKeyName = PrincipalRequestKey(principalKey);
        var member = sessionId + ':' + requestKey + ':' + generation;
        var lifetimeMs = (long)McpRequestBounds.Lifetime.TotalMilliseconds;
        long expires = 0;
        McpRequestState state;
        try
        {
            if (operation == McpRequestOperation.Register)
            {
                expires = (long)await database.ScriptEvaluateAsync(ReservePrincipalScript,
                    [principalKeyName], [member, McpRequestBounds.PerPrincipal, lifetimeMs])
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (expires == 0)
                {
                    return McpRequestState.Capacity;
                }
            }

            var result = await database.ScriptEvaluateAsync(RequestScript, RequestKeys(sessionId),
                [requestKey, generation, operation.ToString(), principalKey, PrincipalHash(principalKey),
                    McpRequestBounds.PerSession, expires, lifetimeMs])
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            state = (McpRequestState)(int)result;
        }
        catch
        {
            if (operation is McpRequestOperation.Register or McpRequestOperation.Finish)
            {
                await ReleaseReservationAsync().ConfigureAwait(false);
            }

            throw;
        }

        if (operation == McpRequestOperation.Finish
            || (operation == McpRequestOperation.Register && state != McpRequestState.Active))
        {
            await ReleaseReservationAsync().ConfigureAwait(false);
        }

        return state;

        async Task ReleaseReservationAsync()
        {
            // Independent of the failed/cancelled operation's token. A lost
            // compensation remains bounded by the reservation's original TTL.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await database.SortedSetRemoveAsync(principalKeyName, member)
                    .WaitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                // Never claim successful admission from an uncertain cleanup.
                // Register failure still fails; Finish's caller already owns no
                // running handler after its completion linearization point.
            }
        }
    }
}
