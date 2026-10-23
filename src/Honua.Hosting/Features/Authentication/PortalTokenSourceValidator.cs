// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Re-checks the credential a stored portal token was minted from (SEC-9). A portal token is
/// a derived credential: it must stop when the credential behind it stops.
/// </summary>
internal interface IPortalTokenSourceValidator
{
    /// <summary>
    /// Whether the credential described by <paramref name="source"/> is still the live one.
    /// Fails closed: anything that cannot be confirmed is reported as no longer valid.
    /// </summary>
    /// <param name="source">Source recorded on the token at issuance.</param>
    /// <param name="cancellationToken">Token used to abort the check.</param>
    /// <returns><see langword="true"/> when the token may still be honoured.</returns>
    ValueTask<bool> IsStillValidAsync(PortalTokenSourceRecord source, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IPortalTokenSourceValidator"/>. Resolves the live credential through the
/// stores that already own it and compares its version marker with the one recorded on the
/// token.
/// </summary>
/// <remarks>
/// <para>
/// The check is bounded by an in-process cache of the LIVE credential's marker, keyed by the
/// credential (not by the token), with an absolute lifetime of
/// <see cref="PortalTokenAuthenticationOptions.SourceRevalidationSeconds"/>. Steady-state cost
/// is therefore at most one point read per distinct source credential per window per instance,
/// not one per request: with the default window a deployment authenticating a thousand portal
/// tokens a second issued from one key performs six key reads a minute, and each read is a
/// single <c>GET</c> — strictly cheaper than the <c>SDIFF</c> + <c>SMEMBERS</c> + <c>MGET</c> +
/// conditional write that <see cref="IAdminApiKeyStore.ValidateAsync"/> already performs when
/// the same key authenticates over <c>X-API-Key</c>.
/// </para>
/// <para>
/// The window is also the worst-case propagation delay for a revocation, which is why it is
/// short and configurable: set it to zero to re-read on every request.
/// </para>
/// </remarks>
internal sealed class PortalTokenSourceValidator(
    IMemoryCache memoryCache,
    IOptions<PortalTokenAuthenticationOptions> portalTokenOptions,
    IServiceProvider serviceProvider,
    TimeProvider? timeProvider = null) : IPortalTokenSourceValidator
{
    private const string CacheKeyPrefix = "portal-auth:source-version:";
    private const string AdminPasswordCacheKey = CacheKeyPrefix + "admin-password";
    private const string ManagedKeyCacheKeyPrefix = CacheKeyPrefix + "managed-key:";

    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly IOptions<PortalTokenAuthenticationOptions> _portalTokenOptions = portalTokenOptions;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async ValueTask<bool> IsStillValidAsync(
        PortalTokenSourceRecord source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        switch (source.Kind)
        {
            case PortalCredentialSourceKind.None:
                // No separately revocable credential stands behind this token; its own
                // lifetime, already checked by the caller, is the bound.
                return true;

            case PortalCredentialSourceKind.FederatedToken:
                // The bridged token is stateless and is not held anywhere to re-read. Its
                // `exp` clamped this token's lifetime at issuance and the record's own
                // ExpiresAt check enforces that clamp on every restore.
                return true;

            case PortalCredentialSourceKind.ManagedApiKey:
                return await MatchesAsync(
                    ManagedKeyCacheKeyPrefix + source.Reference,
                    source.Version,
                    ct => ResolveManagedKeyVersionAsync(source.Reference, ct),
                    cancellationToken).ConfigureAwait(false);

            case PortalCredentialSourceKind.AdminPassword:
                return await MatchesAsync(
                    AdminPasswordCacheKey,
                    source.Version,
                    ResolveAdminPasswordVersionAsync,
                    cancellationToken).ConfigureAwait(false);

            default:
                // An unrecognised kind is a record this build cannot reason about.
                return false;
        }
    }

    private async ValueTask<bool> MatchesAsync(
        string cacheKey,
        string? recordedVersion,
        Func<CancellationToken, Task<string?>> resolveLiveVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(recordedVersion))
        {
            return false;
        }

        if (!_memoryCache.TryGetValue(cacheKey, out LiveSourceVersion? cached) || cached is null)
        {
            string? live;
            try
            {
                live = await resolveLiveVersion(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The credential could not be read (store outage, a rotated value that now
                // violates the credential policy). Refuse rather than honour a snapshot.
                return false;
            }

            cached = new LiveSourceVersion(live);
            var window = ResolveRevalidationWindow();
            if (window > TimeSpan.Zero)
            {
                _memoryCache.Set(
                    cacheKey,
                    cached,
                    new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = window });
            }
        }

        return cached.Version is not null
            && string.Equals(cached.Version, recordedVersion, StringComparison.Ordinal);
    }

    private async Task<string?> ResolveManagedKeyVersionAsync(string? reference, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(reference, out var id))
        {
            return null;
        }

        var store = _serviceProvider.GetService<IAdminApiKeyStore>();
        if (store is null)
        {
            return null;
        }

        var record = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        // Same authority rule the key store applies when the key authenticates directly: the
        // registry retains revoked and expired records for administrative reads, so presence
        // alone is not validity.
        var now = _timeProvider.GetUtcNow();
        if (record.RevokedAt is not null || (record.ExpiresAt.HasValue && record.ExpiresAt.Value <= now))
        {
            return null;
        }

        return PortalCredentialSourceVersion.ForManagedKey(record);
    }

    private async Task<string?> ResolveAdminPasswordVersionAsync(CancellationToken cancellationToken)
    {
        var options = _serviceProvider.GetService<IOptions<ApiKeyAuthenticationOptions>>();
        if (options is null)
        {
            return null;
        }

        var resolved = await PortalCredentialSourceVersion.ResolveAdminPasswordAsync(
            options.Value,
            _serviceProvider.GetService<IConnectionSecretResolver>(),
            cancellationToken).ConfigureAwait(false);

        return string.IsNullOrEmpty(resolved) ? null : PortalCredentialSourceVersion.ForAdminPassword(resolved);
    }

    private TimeSpan ResolveRevalidationWindow()
    {
        var seconds = _portalTokenOptions.Value.SourceRevalidationSeconds;
        return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Cached marker of the live credential; a null marker means "no longer valid".</summary>
    private sealed record LiveSourceVersion(string? Version);
}
