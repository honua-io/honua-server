// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication.ClientCertificates;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed partial class ConfiguredClientCertificateTrustBootstrap(
    PostgresConfigurationDocuments documents, ClientCertificateAuthenticationOptions? options, ILogger? logger)
{
    private const string ManifestKey = "CONFIGURED";
    private const int MaximumAttempts = 16;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private static readonly ConfigurationCollection<ClientCertificateTrustProfile> _profiles =
        new("client-certificate-trust", SharedConfigurationJsonContext.Default.ClientCertificateTrustProfile);
    private static readonly ConfigurationCollection<ConfiguredTrustManifest> _manifests =
        new("client-certificate-trust-configured", SharedConfigurationJsonContext.Default.ConfiguredTrustManifest);

    public async Task<bool> ReconcileAsync(CancellationToken token)
    {
        if (options is null) { return true; }
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var profiles = await documents.ReadAsync(_profiles, token).ConfigureAwait(false);
            var manifests = await documents.ReadAsync(_manifests, token).ConfigureAwait(false);
            if (profiles.Generation != manifests.Generation) { continue; }
            if (manifests.UnreadableKeys.Contains(ManifestKey, StringComparer.Ordinal))
            {
                ConfiguredManifestUnreadable(_logger);
                return false;
            }
            var previous = manifests.Documents.FirstOrDefault(document => document.Key == ManifestKey);
            if (options.TrustProfilesRevision <= (previous?.Value.Revision ?? 0)) { return true; }
            // Preserve ownership until every affected profile can be reconciled atomically.
            var unreadable = profiles.UnreadableKeys.FirstOrDefault(key =>
                options.TrustProfiles.Any(profile => string.Equals(profile.ProfileId, key, StringComparison.OrdinalIgnoreCase))
                || previous?.Value.Profiles.ContainsKey(key) == true);
            if (unreadable is not null)
            {
                ConfiguredProfileUnreadable(_logger, unreadable);
                return false;
            }
            var changes = BuildChanges(profiles, previous);
            if (await documents.WriteBatchAsync(profiles.Generation, changes, token).ConfigureAwait(false)) { return true; }
        }
        throw new InvalidOperationException("Concurrent trust configuration updates exceeded the retry limit.");
    }

    private List<ConfigurationChange> BuildChanges(
        ConfigurationSnapshot<ClientCertificateTrustProfile> snapshot, ConfigurationDocument<ConfiguredTrustManifest>? previous)
    {
        var configuredOptions = options ?? throw new InvalidOperationException("Configured trust options are unavailable.");
        var next = new ConfiguredTrustManifest { Revision = configuredOptions.TrustProfilesRevision };
        var changes = new List<ConfigurationChange>();
        foreach (var configured in configuredOptions.TrustProfiles.Select(ClientCertificateTrustProfile.FromOptions))
        {
            var key = configured.ProfileId.ToUpperInvariant();
            next.Profiles[key] = configured.PrincipalMappings.Select(mapping => mapping.MappingId.ToUpperInvariant()).ToArray();
            var current = snapshot.Documents.FirstOrDefault(document => document.Key == key);
            var formerlyConfigured = previous?.Value.Profiles.GetValueOrDefault(key) ?? [];
            var merged = current is null ? configured : PostgresClientCertificateTrustStore.ApplyConfigured(current.Value, configured, formerlyConfigured);
            if (!ReferenceEquals(merged, current?.Value))
            {
                changes.Add(documents.PrepareWrite(_profiles, new(key, current?.Revision ?? 0, merged)));
            }
        }
        AddRemovedProfiles(changes, snapshot, previous?.Value.Profiles.Keys.Except(next.Profiles.Keys) ?? []);
        changes.Add(documents.PrepareWrite(_manifests, new(ManifestKey, previous?.Revision ?? 0, next)));
        return changes;
    }

    private void AddRemovedProfiles(List<ConfigurationChange> changes, ConfigurationSnapshot<ClientCertificateTrustProfile> snapshot, IEnumerable<string> removed)
    {
        foreach (var key in removed)
        {
            var current = snapshot.Documents.FirstOrDefault(document => document.Key == key);
            if (current?.Value is not { Enabled: true } profile) { continue; }
            changes.Add(documents.PrepareWrite(_profiles,
                new(key, current.Revision, profile with { Enabled = false, Revision = profile.Revision + 1 })));
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The configured client-certificate trust manifest is unreadable; configuration reconciliation requires its repair.")]
    private static partial void ConfiguredManifestUnreadable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Configured client-certificate trust profile {ProfileId} has an unreadable stored copy; it is not trusted until repaired.")]
    private static partial void ConfiguredProfileUnreadable(ILogger logger, string profileId);
}
