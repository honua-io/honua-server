// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

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
            var next = _profiles.TryGetValue(profile.ProfileId, out var existing)
                ? profile with
                {
                    Revision = existing.Revision + 1,
                    PrincipalMappings = profile.PrincipalMappings.Count == 0
                        ? existing.PrincipalMappings
                        : profile.PrincipalMappings,
                    Revocations = profile.Revocations.Count == 0 ? existing.Revocations : profile.Revocations,
                }
                : profile with { Revision = Math.Max(1, profile.Revision) };

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

            var mappings = profile.PrincipalMappings.ToList();
            var index = mappings.FindIndex(existing =>
                string.Equals(existing.MappingId, mapping.MappingId, StringComparison.OrdinalIgnoreCase));
            var nextMapping = index >= 0 ? mapping with { Revision = mappings[index].Revision + 1 } : mapping;
            if (index >= 0)
            {
                mappings[index] = nextMapping;
            }
            else
            {
                mappings.Add(nextMapping);
            }

            _profiles[profileId] = profile with
            {
                PrincipalMappings = mappings.ToArray(),
                Revision = profile.Revision + 1,
            };

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

            var mappings = profile.PrincipalMappings.ToList();
            var index = mappings.FindIndex(existing =>
                string.Equals(existing.MappingId, mappingId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return Task.FromResult<ClientCertificatePrincipalMapping?>(null);
            }

            var nextMapping = mappings[index] with
            {
                Enabled = false,
                Revision = mappings[index].Revision + 1,
            };
            mappings[index] = nextMapping;

            _profiles[profileId] = profile with
            {
                PrincipalMappings = mappings.ToArray(),
                Revision = profile.Revision + 1,
            };

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
