// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed class PostgresOidcProviderStore(PostgresConfigurationDocuments documents, IHttpClientFactory? httpClientFactory = null) : IOidcProviderStore
{
    private readonly ConfigurationCollectionStore<OidcProviderConfiguration> _store = new(documents,
        new("oidc-provider", SharedConfigurationJsonContext.Default.OidcProviderConfiguration));

    public async Task<IReadOnlyList<OidcProviderConfiguration>> ListProvidersAsync(CancellationToken cancellationToken = default)
        => (await _store.ReadAsync(cancellationToken).ConfigureAwait(false)).Documents.Select(document => document.Value).ToArray();

    public async Task<OidcProviderConfiguration?> GetProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
        => (await ListProvidersAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(provider => provider.ProviderId == providerId);

    public Task<OidcProviderConfiguration> CreateProviderAsync(OidcProviderConfiguration provider, CancellationToken cancellationToken = default)
        => _store.CreateAsync(provider.ProviderId.ToString("D"), provider, cancellationToken);

    public Task<OidcProviderConfiguration?> UpdateProviderAsync(OidcProviderConfiguration provider, CancellationToken cancellationToken = default)
        => _store.MutateAsync(provider.ProviderId.ToString("D"), current => current is null ? null : provider, cancellationToken);

    public Task<bool> DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
        => _store.DeleteAsync(providerId.ToString("D"), cancellationToken);

    public async Task<OidcProviderTestResult> TestProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
        => await OidcProviderDiscoveryProbe.TestAsync(await GetProviderAsync(providerId, cancellationToken).ConfigureAwait(false),
            httpClientFactory, cancellationToken).ConfigureAwait(false);
}
