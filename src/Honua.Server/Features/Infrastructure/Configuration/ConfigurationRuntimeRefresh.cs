// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Infrastructure.Configuration;

// A database probe precedes authentication on every request. A failed probe must never use stale identity configuration.
internal sealed class ConfigurationRuntimeRefresh(PostgresConfigurationDocuments documents, OidcRuntimeProviderRegistry registry)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation = -1;

    public async Task<long> RefreshAsync(CancellationToken cancellationToken)
    {
        var generation = await documents.GetGenerationAsync(cancellationToken).ConfigureAwait(false);
        if (generation == Interlocked.Read(ref _generation)) { return generation; }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await documents.ReadAsync(new ConfigurationCollection<Honua.Core.Features.Identity.Domain.OidcProviderConfiguration>(
                "oidc-provider", ConfigurationJsonContext.Default.OidcProviderConfiguration), cancellationToken).ConfigureAwait(false);
            registry.Replace(snapshot.Documents.Select(document => new OidcRuntimeProvider(
                document.Value.ProviderId.ToString("D"), document.Value.ProviderType, document.Value.Name,
                document.Value.Authority, document.Value.ClientId, document.Value.Enabled, document.Value.ClientSecret)).ToArray());
            Interlocked.Exchange(ref _generation, snapshot.Generation);
            return snapshot.Generation;
        }
        finally { _gate.Release(); }
    }
}
