// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
            _seeded = await _bootstrap.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _seedGate.Release(); }
    }

    internal static ClientCertificateTrustProfile ApplyConfigured(
        ClientCertificateTrustProfile stored, ClientCertificateTrustProfile configured, IReadOnlyCollection<string>? formerlyConfigured = null)
    {
        var issuerTrust = MergeIssuerTrust(stored, configured);
        var merged = issuerTrust with
        {
            Revision = stored.Revision,
            Enabled = stored.Enabled && issuerTrust.Enabled,
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
            Revocations = MergeRevocations(stored.Revocations, configured.Revocations)
        };
        var type = SharedConfigurationJsonContext.Default.ClientCertificateTrustProfile;
        return JsonSerializer.Serialize(merged, type) == JsonSerializer.Serialize(stored, type)
            ? stored
            : merged with { Revision = stored.Revision + 1 };
    }

    // Configuration owns mapping identity, roles and scope; validity restrictions on either side win.
    private static ClientCertificatePrincipalMapping MergeMapping(ClientCertificatePrincipalMapping? stored, ClientCertificatePrincipalMapping configured)
        => stored is null ? configured : configured with
        {
            Enabled = stored.Enabled && configured.Enabled,
            NotBefore = stored.NotBefore is { } start && (configured.NotBefore is null || start > configured.NotBefore)
                ? start : configured.NotBefore,
            NotAfter = stored.NotAfter is { } end && (configured.NotAfter is null || end < configured.NotAfter)
                ? end : configured.NotAfter,
            Revision = stored.Revision
        };

    private static ClientCertificateTrustProfile MergeIssuerTrust(ClientCertificateTrustProfile stored, ClientCertificateTrustProfile configured)
    {
        var thumbprints = configured.AcceptedIssuerThumbprints.Intersect(stored.AcceptedIssuerThumbprints, StringComparer.OrdinalIgnoreCase).ToArray();
        var anchors = configured.CustomTrustAnchorCertificates.Intersect(stored.CustomTrustAnchorCertificates, StringComparer.Ordinal).ToArray();
        // Chain hints constrain each other; losing one must not turn the other into a broader fallback.
        if (anchors.Length == 0 && (stored.CustomTrustAnchorCertificates.Count > 0 || configured.CustomTrustAnchorCertificates.Count > 0))
        {
            thumbprints = [];
        }
        if (thumbprints.Length == 0 && (stored.AcceptedIssuerThumbprints.Count > 0 || configured.AcceptedIssuerThumbprints.Count > 0))
        {
            anchors = [];
        }
        var incompatibleRestrictions =
            (anchors.Length == 0 && (stored.CustomTrustAnchorCertificates.Count > 0 || configured.CustomTrustAnchorCertificates.Count > 0)) ||
            (thumbprints.Length == 0 && (stored.AcceptedIssuerThumbprints.Count > 0 || configured.AcceptedIssuerThumbprints.Count > 0));
        return configured with
        {
            Enabled = configured.Enabled && !incompatibleRestrictions,
            AcceptedIssuerThumbprints = thumbprints,
            AcceptedIssuerSubjects = configured.AcceptedIssuerSubjects.Intersect(stored.AcceptedIssuerSubjects, StringComparer.OrdinalIgnoreCase).ToArray(),
            CustomTrustAnchorCertificates = anchors
        };
    }

    private static List<ClientCertificateRevocationEntry> MergeRevocations(
        IReadOnlyList<ClientCertificateRevocationEntry> stored, IReadOnlyList<ClientCertificateRevocationEntry> configured)
    {
        var merged = stored.ToList();
        foreach (var entry in configured)
        {
            var index = merged.FindIndex(existing => Equal(existing.RevocationId, entry.RevocationId));
            if (index < 0)
            {
                merged.Add(entry);
                continue;
            }
            var previous = merged[index];
            if (SameRevocationTarget(previous, entry))
            {
                merged[index] = previous with { Enabled = previous.Enabled || entry.Enabled };
            }
            else if (!previous.Enabled)
            {
                merged[index] = entry;
            }
            else if (entry.Enabled)
            {
                AddConfiguredRevocation(merged, entry);
            }
        }
        return merged;
    }

    private static bool SameRevocationTarget(ClientCertificateRevocationEntry first, ClientCertificateRevocationEntry second)
        => string.Equals(first.FingerprintSha256, second.FingerprintSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.Issuer, second.Issuer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.SerialNumber, second.SerialNumber, StringComparison.OrdinalIgnoreCase);

    private static void AddConfiguredRevocation(List<ClientCertificateRevocationEntry> merged, ClientCertificateRevocationEntry entry)
    {
        // Retain an active older target under its original ID and give the additional target a stable ID.
        var target = string.Join("\n", entry.FingerprintSha256?.ToUpperInvariant(), entry.Issuer?.ToUpperInvariant(), entry.SerialNumber?.ToUpperInvariant());
        var id = $"{entry.RevocationId}:configured:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target)))}";
        var index = merged.FindIndex(existing => Equal(existing.RevocationId, id));
        if (index < 0)
        {
            merged.Add(entry with { RevocationId = id });
        }
        else
        {
            merged[index] = merged[index] with { Enabled = true };
        }
    }

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
