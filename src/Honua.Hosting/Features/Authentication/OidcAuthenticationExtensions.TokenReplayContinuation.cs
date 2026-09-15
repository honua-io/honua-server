// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Token replay surfaces and continuations (honua-server#4909, honua-server#4899). Replay
/// protection records the surface a validated bearer token was first admitted on. On the
/// ordinary HTTP API the token stays reusable for its lifetime, as OAuth bearer clients
/// expect. A surface that owns server-issued continuations (the MCP transport) registers the
/// token as single-use until a continuation — an MCP session whose id the server minted for
/// the authenticated caller — binds it; reuse there is then admitted only when the request
/// presents the same continuation (not another session, no session, or a new
/// <c>initialize</c>). Reuse across surfaces is a replay: a token admitted on the HTTP API
/// cannot open or continue an MCP session, and a token registered on the MCP transport is
/// refused by the HTTP API. The registration lives in the replay store, so it holds across
/// instances when the store is Redis.
/// </summary>
public static partial class OidcAuthenticationExtensions
{
    private const string RegisteredTokenReplayValue = "1";
    private const string HttpApiTokenReplayValue = "surface:http";
    private const string TokenReplayContinuationPrefix = "continuation:";

    // Moves a first-use registration to a continuation, keeping its expiry. Binding the
    // same continuation again is idempotent; a token already bound to a different
    // continuation, or admitted on the HTTP API, is never moved, so a replayed request
    // cannot steal another session's binding.
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
    /// registered for replay protection on a continuation surface for this request (API key,
    /// anonymous, replay protection disabled, a token admitted on the HTTP API) or when the
    /// token is already bound elsewhere.
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

    private static TokenReplayScope ResolveTokenReplayScope(HttpContext context)
    {
        foreach (var resolver in context.RequestServices.GetServices<ITokenReplayContinuationResolver>())
        {
            if (!resolver.OwnsRequest(context))
            {
                continue;
            }

            var continuationId = resolver.ResolveContinuationId(context);
            return TokenReplayScope.ContinuationSurface(
                string.IsNullOrWhiteSpace(continuationId) ? null : BuildTokenReplayContinuationMarker(continuationId));
        }

        return TokenReplayScope.HttpApi;
    }

    private static TokenReplayRegistrationResult ClassifyReusedToken(object? current, TokenReplayScope scope)
    {
        var value = current as string;
        if (scope.ContinuationMarker is not null
            && string.Equals(value, scope.ContinuationMarker, StringComparison.Ordinal))
        {
            return TokenReplayRegistrationResult.Continued;
        }

        return !scope.OwnsContinuations && string.Equals(value, HttpApiTokenReplayValue, StringComparison.Ordinal)
            ? TokenReplayRegistrationResult.Reused
            : TokenReplayRegistrationResult.ReplayDetected;
    }

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

    /// <summary>
    /// The replay surface a request presents its bearer token on (honua-server#4899): the
    /// ordinary HTTP API, or a surface that owns server-issued continuations together with
    /// the digest of the continuation the request continues, if any.
    /// </summary>
    internal readonly record struct TokenReplayScope(bool OwnsContinuations, string? ContinuationMarker)
    {
        public static TokenReplayScope HttpApi => new(false, null);

        public static TokenReplayScope ContinuationSurface(string? continuationMarker) =>
            new(true, continuationMarker);

        /// <summary>Replay-store value recorded when a token is first admitted on this surface.</summary>
        public string RegistrationValue => OwnsContinuations ? RegisteredTokenReplayValue : HttpApiTokenReplayValue;
    }

    internal enum TokenReplayStore
    {
        Redis,
        Memory
    }
}

/// <summary>
/// A surface that owns server-issued continuations (for example the MCP transport and its
/// sessions). Implementations must only claim the routes of that surface: a token admitted
/// there stays single-use outside its continuation and is a replay on the ordinary HTTP API,
/// and a token admitted on the HTTP API is a replay there.
/// </summary>
internal interface ITokenReplayContinuationResolver
{
    /// <summary>Whether <paramref name="context"/> targets this continuation surface.</summary>
    bool OwnsRequest(HttpContext context);

    /// <summary>
    /// Names the continuation the request presents, or <see langword="null"/> when it
    /// continues none (for example an MCP <c>initialize</c>) or is not on this surface.
    /// </summary>
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
