// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Infrastructure.Redis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace Honua.Infrastructure.Authentication;

// An ordinary API token stays reusable, a continuation surface registers it as single-use until the server-minted continuation binds it, and reuse across surfaces is a replay.
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
        if (!admission.Scope.CanBindContinuation)
        {
            return false;
        }

        var marker = BuildTokenReplayContinuationMarker(continuationId);
        if (admission.Store == TokenReplayStore.Redis)
        {
            if (redis is null)
            {
                return false;
            }

            try
            {
                var bound = await RedisClientFault.RetryCompletedReaderAsync(
                    () => redis.GetDatabase().ScriptEvaluateAsync(
                        BindTokenReplayContinuationScript,
                        [admission.TokenKey],
                        [RegisteredTokenReplayValue, marker]),
                    cancellationToken).ConfigureAwait(false);
                return (int)bound == 1;
            }
            catch (Exception ex) when (RedisClientFault.IsRedisTransportFault(ex))
            {
                // RedisTimeoutException derives from TimeoutException, not RedisException.
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

            if (current is RegisteredTokenReplayValue)
            {
                memoryCache.Set(admission.TokenKey, marker, new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = new DateTimeOffset(admission.ExpiresOn)
                });
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    // The replay store keeps only the continuation digest because the continuation id names a live session.
    internal static string BuildTokenReplayContinuationMarker(string continuationId) =>
        TokenReplayContinuationPrefix
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(continuationId)));

    private static TokenReplayScope ResolveTokenReplayScope(HttpContext context, SecurityToken token)
    {
        foreach (var resolver in context.RequestServices.GetServices<ITokenReplayScopeResolver>())
        {
            if (!resolver.OwnsRequest(context))
            {
                continue;
            }

            return resolver.ResolveScope(context, EncodedTokenFingerprint(token));
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

        return !scope.CanBindContinuation && string.Equals(value, scope.RegistrationValue, StringComparison.Ordinal)
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

    internal readonly record struct TokenReplayScope(string RegistrationValue, string? ContinuationMarker)
    {
        public static TokenReplayScope HttpApi => new(HttpApiTokenReplayValue, null);

        public static TokenReplayScope ContinuationSurface(string? continuationMarker) =>
            new(RegisteredTokenReplayValue, continuationMarker);

        public static TokenReplayScope ReusableSurface(string surface, string validatedTokenFingerprint) =>
            new($"surface:{surface}:{validatedTokenFingerprint}", null);

        public bool CanBindContinuation => RegistrationValue == RegisteredTokenReplayValue;
    }

    internal enum TokenReplayStore
    {
        Redis,
        Memory
    }
}

// An implementation may claim only its own routes, because admission there makes the token a replay on the ordinary API and an API admission is a replay on that surface.
internal interface ITokenReplayScopeResolver
{
    bool OwnsRequest(HttpContext context);

    OidcAuthenticationExtensions.TokenReplayScope ResolveScope(HttpContext context, string? validatedTokenFingerprint);
}

// The continuation binding is created by the server and a client cannot supply it.
internal sealed class TokenReplayAdmissionFeature(
    string tokenKey,
    DateTime expiresOn,
    OidcAuthenticationExtensions.TokenReplayStore store,
    OidcAuthenticationExtensions.TokenReplayScope? scope = null)
{
    public string TokenKey { get; } = tokenKey;
    public DateTime ExpiresOn { get; } = expiresOn;
    public OidcAuthenticationExtensions.TokenReplayStore Store { get; } = store;
    public OidcAuthenticationExtensions.TokenReplayScope Scope { get; } =
        scope ?? OidcAuthenticationExtensions.TokenReplayScope.ContinuationSurface(null);
}
