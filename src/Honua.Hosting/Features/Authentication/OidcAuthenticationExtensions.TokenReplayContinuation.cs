// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Token replay continuations (honua-server#4909). Replay protection registers every
/// validated bearer token as single-use. A server-issued continuation — an MCP session
/// whose id the server minted for the authenticated caller — may bind the token admitted
/// on the request that created or continued it. Reuse of that token is then admitted only
/// when the request presents the same continuation; reuse anywhere else (another route, no
/// session, another session, a new <c>initialize</c>) is still rejected as a replay.
/// The binding lives in the same replay store as the registration, so it holds across
/// instances when the store is Redis.
/// </summary>
public static partial class OidcAuthenticationExtensions
{
    private const string RegisteredTokenReplayValue = "1";
    private const string TokenReplayContinuationPrefix = "continuation:";

    // Moves a first-use registration to a continuation, keeping its expiry. Binding the
    // same continuation again is idempotent; a token already bound to a different
    // continuation is never moved, so a replayed request cannot steal another session's
    // binding.
    private const string BindTokenReplayContinuationScript = """
        local current = redis.call('GET', KEYS[1])
        if current == ARGV[2] then return 1 end
        if current ~= ARGV[1] then return 0 end
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl <= 0 then return 0 end
        redis.call('SET', KEYS[1], ARGV[2], 'PX', ttl)
        return 1
        """;

    /// <summary>
    /// Binds the bearer token admitted on <paramref name="context"/> to a server-issued
    /// continuation. Returns <see langword="false"/> without effect when no token was
    /// registered for replay protection on this request (API key, anonymous, replay
    /// protection disabled) or when the token is already bound elsewhere.
    /// </summary>
    internal static Task<bool> TryBindTokenReplayContinuationAsync(HttpContext context, string continuationId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var admission = context.Features.Get<TokenReplayAdmissionFeature>();
        if (admission is null || string.IsNullOrWhiteSpace(continuationId))
        {
            return Task.FromResult(false);
        }

        return TryBindTokenReplayContinuationAsync(
            admission,
            continuationId,
            context.RequestServices.GetService<IConnectionMultiplexer>(),
            context.RequestServices.GetService<IMemoryCache>(),
            context.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>(),
            context.RequestAborted);
    }

    internal static async Task<bool> TryBindTokenReplayContinuationAsync(
        TokenReplayAdmissionFeature admission,
        string continuationId,
        IConnectionMultiplexer? redis,
        IMemoryCache? memoryCache,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var marker = BuildTokenReplayContinuationMarker(continuationId);
        if (admission.Store == TokenReplayStore.Redis)
        {
            if (redis is null)
            {
                return false;
            }

            try
            {
                var bound = await redis.GetDatabase().ScriptEvaluateAsync(
                    BindTokenReplayContinuationScript,
                    [admission.TokenKey],
                    [RegisteredTokenReplayValue, marker]).ConfigureAwait(false);
                return (int)bound == 1;
            }
            catch (RedisException ex)
            {
                OidcAuthenticationLog.TokenReplayContinuationBindFailed(logger, ex);
                return false;
            }
        }

        if (memoryCache is null)
        {
            return false;
        }

        return await WithReplayLockAsync(admission.TokenKey, () =>
        {
            if (!memoryCache.TryGetValue(admission.TokenKey, out var current)
                || (current is not RegisteredTokenReplayValue
                    && !string.Equals(current as string, marker, StringComparison.Ordinal)))
            {
                return false;
            }

            memoryCache.Set(admission.TokenKey, marker, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = new DateTimeOffset(admission.ExpiresOn)
            });
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replay-store value for a continuation. The continuation id names a live session,
    /// so only its digest is stored.
    /// </summary>
    internal static string BuildTokenReplayContinuationMarker(string continuationId) =>
        TokenReplayContinuationPrefix
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(continuationId)));

    private static string? ResolveTokenReplayContinuationMarker(HttpContext context)
    {
        foreach (var resolver in context.RequestServices.GetServices<ITokenReplayContinuationResolver>())
        {
            var continuationId = resolver.ResolveContinuationId(context);
            if (!string.IsNullOrWhiteSpace(continuationId))
            {
                return BuildTokenReplayContinuationMarker(continuationId);
            }
        }

        return null;
    }

    private static TokenReplayRegistrationResult ClassifyReusedToken(object? current, string? continuationMarker) =>
        continuationMarker is not null && string.Equals(current as string, continuationMarker, StringComparison.Ordinal)
            ? TokenReplayRegistrationResult.Continued
            : TokenReplayRegistrationResult.ReplayDetected;

    private static async Task<T> WithReplayLockAsync<T>(
        string tokenKey,
        Func<T> action,
        CancellationToken cancellationToken)
    {
        var replayLock = AcquireReplayLock(tokenKey);

        try
        {
            await replayLock.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return action();
            }
            finally
            {
                replayLock.Semaphore.Release();
            }
        }
        finally
        {
            // Tombstone the state (0 -> Tombstone) before removing/disposing so a
            // concurrent acquirer that already obtained this instance via GetOrAdd can
            // never increment past the tombstone and wait on a disposed semaphore.
            // If an acquirer raced us and raised the count first, the CAS fails and
            // that acquirer (or the last one to release) performs the cleanup instead.
            if (Interlocked.Decrement(ref replayLock.ReferenceCount) == 0 &&
                Interlocked.CompareExchange(ref replayLock.ReferenceCount, ReplayLockState.Tombstone, 0) == 0)
            {
                TokenReplayLocks.TryRemove(new KeyValuePair<string, ReplayLockState>(tokenKey, replayLock));
                replayLock.Semaphore.Dispose();
            }
        }
    }

    internal readonly record struct TokenReplayRegistration(
        TokenReplayRegistrationResult Result,
        TokenReplayStore? Store);

    internal enum TokenReplayStore
    {
        Redis,
        Memory
    }
}

/// <summary>
/// Names the server-issued continuation (for example an MCP session) a request presents,
/// or <see langword="null"/> when the request continues none. Implementations must only
/// answer for the routes that own the continuation, so a bound token stays single-use
/// everywhere else.
/// </summary>
internal interface ITokenReplayContinuationResolver
{
    string? ResolveContinuationId(HttpContext context);
}

/// <summary>
/// Server-created request feature recording the replay-store entry of the bearer token
/// admitted on this request. Clients cannot supply it.
/// </summary>
internal sealed record TokenReplayAdmissionFeature(
    string TokenKey,
    DateTime ExpiresOn,
    OidcAuthenticationExtensions.TokenReplayStore Store);
