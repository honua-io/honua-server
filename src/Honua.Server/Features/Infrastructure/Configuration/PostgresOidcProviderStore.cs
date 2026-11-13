// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed class PostgresOidcProviderStore(PostgresConfigurationDocuments documents, IHttpClientFactory? httpClientFactory = null) : IOidcProviderStore
{
    private static readonly ConfigurationCollection<OidcProviderConfiguration> Collection =
        new("oidc-provider", SharedConfigurationJsonContext.Default.OidcProviderConfiguration);
    private readonly ConfigurationCollectionStore<OidcProviderConfiguration> _store = new(documents, Collection);

    public async Task<IReadOnlyList<OidcProviderConfiguration>> ListProvidersAsync(CancellationToken cancellationToken = default)
        => (await _store.ReadAsync(cancellationToken).ConfigureAwait(false)).Documents.Select(document => document.Value).ToArray();

    public async Task<OidcProviderConfiguration?> GetProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
        => (await ListProvidersAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(provider => provider.ProviderId == providerId);

    public Task<OidcProviderConfiguration> CreateProviderAsync(OidcProviderConfiguration provider, CancellationToken cancellationToken = default)
        => _store.CreateAsync(provider.ProviderId.ToString("D"), provider, cancellationToken);

    internal async Task<OidcProviderConfiguration?> CreateProviderIfBelowLimitAsync(
        OidcProviderConfiguration provider, int maximumProviders, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumProviders);
        var write = documents.PrepareWrite(Collection, new(provider.ProviderId.ToString("D"), 0, provider));
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            // Undecryptable rows remain configured providers and consume the same entitlement capacity.
            if (snapshot.Documents.Count + snapshot.UnreadableKeys.Count >= maximumProviders)
            {
                return null;
            }
            if (await documents.WriteBatchAsync(snapshot.Generation, [write], cancellationToken).ConfigureAwait(false))
            {
                return provider;
            }
        }
        throw new InvalidOperationException("Concurrent configuration updates exceeded the retry limit.");
    }

    public Task<OidcProviderConfiguration?> UpdateProviderAsync(Guid providerId, Func<OidcProviderConfiguration, OidcProviderConfiguration> update, CancellationToken cancellationToken = default)
        => _store.MutateAsync(providerId.ToString("D"), current => current is null ? null : update(current), cancellationToken);

    public Task<bool> DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
        => _store.DeleteAsync(providerId.ToString("D"), cancellationToken);

    public async Task<OidcProviderTestResult> TestProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
        => await OidcProviderDiscoveryProbe.TestAsync(await GetProviderAsync(providerId, cancellationToken).ConfigureAwait(false),
            httpClientFactory, cancellationToken).ConfigureAwait(false);
}
