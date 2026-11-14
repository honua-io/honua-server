// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal sealed class InMemoryClientCertificateTrustStore : IClientCertificateTrustStore
{
    private readonly ConcurrentDictionary<string, ClientCertificateTrustProfile> _profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public InMemoryClientCertificateTrustStore(IOptions<ClientCertificateAuthenticationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var profile in options.Value.TrustProfiles.Select(ClientCertificateTrustProfile.FromOptions))
        {
            _profiles[profile.ProfileId] = profile;
        }
    }

    public Task<IReadOnlyList<ClientCertificateTrustProfile>> ListProfilesAsync(
        string? environmentId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ClientCertificateTrustProfile> profiles = _profiles.Values
            .Where(profile => string.IsNullOrWhiteSpace(environmentId) ||
                string.Equals(profile.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static profile => profile.EnvironmentId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(profiles);
    }

    public Task<ClientCertificateTrustProfile?> GetProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _profiles.TryGetValue(profileId, out var profile);
        return Task.FromResult(profile);
    }

    public Task<ClientCertificateTrustProfile> UpsertProfileAsync(
        ClientCertificateTrustProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _profiles.TryGetValue(profile.ProfileId, out var existing);
            var next = ClientCertificateTrustMutations.Upsert(existing, profile);

            _profiles[profile.ProfileId] = next;
            return Task.FromResult(next);
        }
    }

    public Task<ClientCertificateTrustProfile?> DisableProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_profiles.TryGetValue(profileId, out var existing))
            {
                return Task.FromResult<ClientCertificateTrustProfile?>(null);
            }

            var next = existing with { Enabled = false, Revision = existing.Revision + 1 };
            _profiles[profileId] = next;
            return Task.FromResult<ClientCertificateTrustProfile?>(next);
        }
    }

    public Task<ClientCertificatePrincipalMapping?> UpsertMappingAsync(
        string profileId,
        ClientCertificatePrincipalMapping mapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_profiles.TryGetValue(profileId, out var profile))
            {
                return Task.FromResult<ClientCertificatePrincipalMapping?>(null);
            }

            var next = ClientCertificateTrustMutations.UpsertMapping(profile, mapping);
            _profiles[profileId] = next;
            var nextMapping = next.PrincipalMappings.First(candidate =>
                string.Equals(candidate.MappingId, mapping.MappingId, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult<ClientCertificatePrincipalMapping?>(nextMapping);
        }
    }

    public Task<ClientCertificatePrincipalMapping?> DisableMappingAsync(
        string profileId,
        string mappingId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_profiles.TryGetValue(profileId, out var profile))
            {
                return Task.FromResult<ClientCertificatePrincipalMapping?>(null);
            }

            var next = ClientCertificateTrustMutations.DisableMapping(profile, mappingId);
            _profiles[profileId] = next;
            var nextMapping = next.PrincipalMappings.FirstOrDefault(candidate =>
                string.Equals(candidate.MappingId, mappingId, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult<ClientCertificatePrincipalMapping?>(nextMapping);
        }
    }

    public Task<ClientCertificateRevocationEntry?> AddRevocationAsync(
        string profileId,
        ClientCertificateRevocationEntry revocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_profiles.TryGetValue(profileId, out var profile))
            {
                return Task.FromResult<ClientCertificateRevocationEntry?>(null);
            }

            var revocations = profile.Revocations
                .Where(existing => !string.Equals(existing.RevocationId, revocation.RevocationId, StringComparison.OrdinalIgnoreCase))
                .Append(revocation)
                .ToArray();

            _profiles[profileId] = profile with
            {
                Revocations = revocations,
                Revision = profile.Revision + 1,
            };

            return Task.FromResult<ClientCertificateRevocationEntry?>(revocation);
        }
    }

    public Task<bool> RemoveRevocationAsync(
        string profileId,
        string revocationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_profiles.TryGetValue(profileId, out var profile))
            {
                return Task.FromResult(false);
            }

            var revocations = profile.Revocations
                .Where(existing => !string.Equals(existing.RevocationId, revocationId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (revocations.Length == profile.Revocations.Count)
            {
                return Task.FromResult(false);
            }

            _profiles[profileId] = profile with
            {
                Revocations = revocations,
                Revision = profile.Revision + 1,
            };

            return Task.FromResult(true);
        }
    }
}
