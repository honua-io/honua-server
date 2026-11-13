// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed partial class PostgresClientCertificateTrustStore(
    PostgresConfigurationDocuments documents,
    IOptions<ClientCertificateAuthenticationOptions>? options = null,
    IHttpContextAccessor? contexts = null,
    ILogger<PostgresClientCertificateTrustStore>? logger = null) : IClientCertificateTrustStore, IDisposable
{
    private readonly ConfiguredClientCertificateTrustBootstrap _bootstrap = new(documents, options?.Value, logger);
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
            if (current is null)
            {
                removed = false;
                return null;
            }
            removed = current.Revocations.Any(candidate => Equal(candidate.RevocationId, revocationId));
            return !removed ? current : current with
            {
                Revision = current.Revision + 1,
                Revocations = current.Revocations.Where(candidate => !Equal(candidate.RevocationId, revocationId)).ToArray()
            };
        }, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    private async Task SeedProfilesAsync(CancellationToken cancellationToken)
    {
        if (_seeded) { return; }
        await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_seeded) { return; }
            await _bootstrap.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            _seeded = true;
        }
        finally { _seedGate.Release(); }
    }

    internal static ClientCertificateTrustProfile ApplyConfigured(
        ClientCertificateTrustProfile stored, ClientCertificateTrustProfile configured, IReadOnlyCollection<string>? formerlyConfigured = null)
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
                .Concat(stored.PrincipalMappings.Where(existing => !configured.PrincipalMappings.Any(mapping => Equal(mapping.MappingId, existing.MappingId)))
                    .Select(existing => existing.Enabled && formerlyConfigured?.Contains(Key(existing.MappingId), StringComparer.Ordinal) == true
                        ? existing with { Enabled = false, Revision = existing.Revision + 1 } : existing))
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

    private static string Key(string profileId) => profileId.ToUpperInvariant();
    private static bool Equal(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    public void Dispose() => _seedGate.Dispose();
}
