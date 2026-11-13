// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed partial class PostgresClientCertificateTrustStore(
    PostgresConfigurationDocuments documents,
    IOptions<ClientCertificateAuthenticationOptions>? options = null,
    IHttpContextAccessor? contexts = null,
    ILogger<PostgresClientCertificateTrustStore>? logger = null) : IClientCertificateTrustStore, IDisposable
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly ConfigurationCollectionStore<ClientCertificateTrustProfile> _store = new(documents,
        new("client-certificate-trust", SharedConfigurationJsonContext.Default.ClientCertificateTrustProfile), contexts);

    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private volatile bool _seeded;

    public async Task<IReadOnlyList<ClientCertificateTrustProfile>> ListProfilesAsync(string? environmentId = null, CancellationToken cancellationToken = default)
    {
        await SeedProfilesAsync(cancellationToken).ConfigureAwait(false);
        return (await _store.ReadCurrentAsync(cancellationToken).ConfigureAwait(false)).Documents.Select(document => document.Value)
            .Where(profile => string.IsNullOrWhiteSpace(environmentId) || string.Equals(profile.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(profile => profile.EnvironmentId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ClientCertificateTrustProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
        => (await ListProfilesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(profile => string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

    public async Task<ClientCertificateTrustProfile> UpsertProfileAsync(ClientCertificateTrustProfile profile, CancellationToken cancellationToken = default)
    {
        await SeedProfilesAsync(cancellationToken).ConfigureAwait(false);
        return (await _store.MutateAsync(Key(profile.ProfileId), current => ClientCertificateTrustMutations.Upsert(current, profile), cancellationToken)
            .ConfigureAwait(false))!;
    }

    public async Task<ClientCertificateTrustProfile?> DisableProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await SeedProfilesAsync(cancellationToken).ConfigureAwait(false);
        return await _store.MutateAsync(Key(profileId), current => current is null ? null : current with { Enabled = false, Revision = current.Revision + 1 }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClientCertificatePrincipalMapping?> UpsertMappingAsync(string profileId, ClientCertificatePrincipalMapping mapping, CancellationToken cancellationToken = default)
    {
        await SeedProfilesAsync(cancellationToken).ConfigureAwait(false);
        var result = await _store.MutateAsync(Key(profileId), current => current is null ? null : ClientCertificateTrustMutations.UpsertMapping(current, mapping), cancellationToken).ConfigureAwait(false);
        return result?.PrincipalMappings.First(candidate => Equal(candidate.MappingId, mapping.MappingId));
    }

    public async Task<ClientCertificatePrincipalMapping?> DisableMappingAsync(string profileId, string mappingId, CancellationToken cancellationToken = default)
    {
        await SeedProfilesAsync(cancellationToken).ConfigureAwait(false);
        var result = await _store.MutateAsync(Key(profileId), current => current is null ? null : ClientCertificateTrustMutations.DisableMapping(current, mappingId), cancellationToken).ConfigureAwait(false);
        return result?.PrincipalMappings.FirstOrDefault(candidate => Equal(candidate.MappingId, mappingId));
    }

    public async Task<ClientCertificateRevocationEntry?> AddRevocationAsync(string profileId, ClientCertificateRevocationEntry revocation, CancellationToken cancellationToken = default)
    {
        await SeedProfilesAsync(cancellationToken).ConfigureAwait(false);
        var result = await _store.MutateAsync(Key(profileId), current => current is null ? null : current with
        {
            Revision = current.Revision + 1,
            Revocations = current.Revocations.Where(candidate => !Equal(candidate.RevocationId, revocation.RevocationId)).Append(revocation).ToArray()
        }, cancellationToken).ConfigureAwait(false);
        return result is null ? null : revocation;
    }

    public async Task<bool> RemoveRevocationAsync(string profileId, string revocationId, CancellationToken cancellationToken = default)
    {
        await SeedProfilesAsync(cancellationToken).ConfigureAwait(false);
        var removed = false;
        await _store.MutateAsync(Key(profileId), current =>
        {
            removed = current?.Revocations.Any(candidate => Equal(candidate.RevocationId, revocationId)) == true;
            return !removed ? current : current! with
            {
                Revision = current.Revision + 1,
                Revocations = current.Revocations.Where(candidate => !Equal(candidate.RevocationId, revocationId)).ToArray()
            };
        }, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    // Configured profiles are applied once per process. Configuration owns issuers, trust anchors and the mappings
    // it defines. For every security setting both sides can change, the stricter value wins, so neither a restart
    // nor an administrator can silently weaken the other's choice. Revocations and admin-only mappings are kept.
    private async Task SeedProfilesAsync(CancellationToken cancellationToken)
    {
        if (_seeded) { return; }
        await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_seeded) { return; }
            var unreadable = (await _store.ReadAsync(cancellationToken).ConfigureAwait(false)).UnreadableKeys;
            foreach (var configured in options?.Value.TrustProfiles.Select(ClientCertificateTrustProfile.FromOptions) ?? [])
            {
                var key = Key(configured.ProfileId);
                if (unreadable.Contains(key, StringComparer.Ordinal))
                {
                    // The stored row stays authoritative and unreadable profiles are not trusted; repair needs the key ring.
                    ConfiguredProfileUnreadable(_logger, configured.ProfileId);
                    continue;
                }
                await _store.MutateAsync(key,
                    current => current is null ? configured : ApplyConfigured(current, configured), cancellationToken).ConfigureAwait(false);
            }
            _seeded = true;
        }
        finally { _seedGate.Release(); }
    }

    internal static ClientCertificateTrustProfile ApplyConfigured(ClientCertificateTrustProfile stored, ClientCertificateTrustProfile configured)
    {
        var merged = configured with
        {
            Revision = stored.Revision,
            Enabled = stored.Enabled && configured.Enabled,
            AllowedSanTypes = configured.AllowedSanTypes.Intersect(stored.AllowedSanTypes).ToArray(),
            AllowSubjectFallback = stored.AllowSubjectFallback && configured.AllowSubjectFallback,
            RequireClientAuthenticationEku = stored.RequireClientAuthenticationEku || configured.RequireClientAuthenticationEku,
            RequireChainTrust = stored.RequireChainTrust || configured.RequireChainTrust,
            ChainRevocationMode = Strictness(stored.ChainRevocationMode) >= Strictness(configured.ChainRevocationMode)
                ? stored.ChainRevocationMode : configured.ChainRevocationMode,
            RevocationStatusUnknownIsFatal = stored.RevocationStatusUnknownIsFatal || configured.RevocationStatusUnknownIsFatal,
            PrincipalMappings = configured.PrincipalMappings
                .Select(mapping => MergeMapping(stored.PrincipalMappings.FirstOrDefault(existing => Equal(existing.MappingId, mapping.MappingId)), mapping))
                .Concat(stored.PrincipalMappings.Where(existing => !configured.PrincipalMappings.Any(mapping => Equal(mapping.MappingId, existing.MappingId))))
                .ToArray(),
            Revocations = stored.Revocations
                .Concat(configured.Revocations.Where(revocation => !stored.Revocations.Any(existing => Equal(existing.RevocationId, revocation.RevocationId))))
                .ToArray()
        };
        var type = SharedConfigurationJsonContext.Default.ClientCertificateTrustProfile;
        return JsonSerializer.Serialize(merged, type) == JsonSerializer.Serialize(stored, type)
            ? stored
            : merged with { Revision = stored.Revision + 1 };
    }

    // A mapping defined in configuration takes its identity, roles and scope from configuration; a disable on either side wins.
    private static ClientCertificatePrincipalMapping MergeMapping(ClientCertificatePrincipalMapping? stored, ClientCertificatePrincipalMapping configured)
        => stored is null ? configured : configured with
        {
            Enabled = stored.Enabled && configured.Enabled,
            Revision = stored.Revision
        };

    private static int Strictness(X509RevocationMode mode) => mode switch
    {
        X509RevocationMode.Online => 2,
        X509RevocationMode.Offline => 1,
        _ => 0
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Configured client-certificate trust profile {ProfileId} has an unreadable stored copy; it is not trusted until repaired.")]
    private static partial void ConfiguredProfileUnreadable(ILogger logger, string profileId);

    private static string Key(string profileId) => profileId.ToUpperInvariant();
    private static bool Equal(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    public void Dispose() => _seedGate.Dispose();
}
