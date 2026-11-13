// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication.ClientCertificates;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed class PostgresClientCertificateTrustStore(PostgresConfigurationDocuments documents, IOptions<ClientCertificateAuthenticationOptions>? options = null) : IClientCertificateTrustStore
{
    private readonly ConfigurationCollectionStore<ClientCertificateTrustProfile> _store = new(documents,
        new("client-certificate-trust", SharedConfigurationJsonContext.Default.ClientCertificateTrustProfile));

    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private bool _seeded;

    public async Task<IReadOnlyList<ClientCertificateTrustProfile>> ListProfilesAsync(string? environmentId = null, CancellationToken cancellationToken = default)
    {
        await SeedProfilesAsync(cancellationToken).ConfigureAwait(false);
        return (await _store.ReadAsync(cancellationToken).ConfigureAwait(false)).Documents.Select(document => document.Value)
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

    // Import bootstrap profiles once, without ever replacing shared mappings, disabled profiles or revocations.
    private async Task SeedProfilesAsync(CancellationToken cancellationToken)
    {
        await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_seeded) { return; }
            foreach (var profile in options?.Value.TrustProfiles.Select(ClientCertificateTrustProfile.FromOptions) ?? [])
            {
                await documents.WriteAsync(new ConfigurationCollection<ClientCertificateTrustProfile>(
                    "client-certificate-trust", SharedConfigurationJsonContext.Default.ClientCertificateTrustProfile),
                    new(Key(profile.ProfileId), 0, profile), cancellationToken).ConfigureAwait(false);
            }
            _seeded = true;
        }
        finally { _seedGate.Release(); }
    }

    private static string Key(string profileId) => profileId.ToUpperInvariant();
    private static bool Equal(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
