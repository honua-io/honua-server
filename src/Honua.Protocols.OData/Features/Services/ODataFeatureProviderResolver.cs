// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.OData.Services;

/// <summary>
/// Resolves OData feature operations through the provider selected by a layer's
/// Metadata v2 storage binding.
/// </summary>
internal sealed class ODataFeatureProviderResolver(
    IFeatureReader fallbackReader,
    FeatureProviderQueryRouter? providerQueryRouter)
{
    private readonly IFeatureReader _fallbackReader = fallbackReader ?? throw new ArgumentNullException(nameof(fallbackReader));
    private readonly FeatureProviderQueryRouter? _providerQueryRouter = providerQueryRouter;

    public async Task<IFeatureReader> ResolveReaderAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service? service,
        MetadataV2Resource resource,
        MetadataV2Publication? publication,
        int storageLayerId,
        FeatureProviderReadOperation operation,
        CancellationToken cancellationToken)
    {
        if (_providerQueryRouter is null ||
            service is null ||
            publication is null ||
            string.IsNullOrEmpty(publication.StorageBindingId))
        {
            return _fallbackReader;
        }

        return await _providerQueryRouter.ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool Supported, string? ErrorMessage)> CheckWriteSupportAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service? service,
        MetadataV2Resource resource,
        MetadataV2Publication? publication,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        if (_providerQueryRouter is null ||
            service is null ||
            publication is null ||
            string.IsNullOrEmpty(publication.StorageBindingId))
        {
            return (true, null);
        }

        var reader = await ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);

        return reader is IFeatureWriter
            ? (true, null)
            : (false, "The layer's configured data provider is read-only for OData write operations.");
    }
}
