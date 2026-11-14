// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

// A portal token stops being honoured when the credential it was minted from stops.
internal interface IPortalTokenSourceValidator
{
    ValueTask<bool> IsStillValidAsync(PortalTokenSourceRecord source, CancellationToken cancellationToken);
}

// Every restore reads the credential authority; a prior successful read grants no grace period.
internal sealed class PortalTokenSourceValidator(
    IServiceProvider serviceProvider,
    TimeProvider? timeProvider = null) : IPortalTokenSourceValidator
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
                    source.Version,
                    ct => ResolveManagedKeyVersionAsync(source.Reference, ct),
                    cancellationToken).ConfigureAwait(false);

            case PortalCredentialSourceKind.AdminPassword:
                return await MatchesAsync(
                    source.Version,
                    ResolveAdminPasswordVersionAsync,
                    cancellationToken).ConfigureAwait(false);

            default:
                // An unrecognised kind is a record this build cannot reason about.
                return false;
        }
    }

    private async ValueTask<bool> MatchesAsync(
        string? recordedVersion,
        Func<CancellationToken, Task<string?>> resolveLiveVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(recordedVersion))
        {
            return false;
        }

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
            // An unavailable authority cannot validate the stored credential snapshot.
            return false;
        }

        return live is not null
            && string.Equals(live, recordedVersion, StringComparison.Ordinal);
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

        // A restricted key cannot be exchanged for a token (ManagedKeyExchangeAuthority), so a
        // token minted before restrictions existed on the key must not outlive them either.
        if (ApiKeyRequestRestrictions.IsRestricted(record))
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

        var configured = options.Value.AdminPassword;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var resolver = _serviceProvider.GetService<IConnectionSecretResolver>();
        var resolved = resolver is not null && await resolver.CanResolveSecretAsync(configured, cancellationToken).ConfigureAwait(false)
            ? await resolver.ResolveSecretFreshAsync(configured, cancellationToken).ConfigureAwait(false)
            : configured;
        AdminPasswordValidation.ValidateRefreshedPassword(resolved, options.Value.EnvironmentName);

        return string.IsNullOrEmpty(resolved) ? null : PortalCredentialSourceVersion.ForAdminPassword(resolved);
    }
}
