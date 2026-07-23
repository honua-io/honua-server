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
    IFeatureWriter fallbackWriter,
    FeatureProviderQueryRouter? providerQueryRouter)
{
    private readonly IFeatureReader _fallbackReader = fallbackReader ?? throw new ArgumentNullException(nameof(fallbackReader));
    private readonly IFeatureWriter _fallbackWriter = fallbackWriter ?? throw new ArgumentNullException(nameof(fallbackWriter));
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
        if (!RequiresProviderRouting(snapshot, publication))
        {
            return _fallbackReader;
        }

        var router = RequireProviderRouter(service, publication);

        return await router.ResolveReaderAsync(
            snapshot,
            service!,
            resource,
            publication!,
            storageLayerId,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IFeatureReader> ResolveQueryReaderAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service? service,
        MetadataV2Resource resource,
        MetadataV2Publication? publication,
        int storageLayerId,
        bool requireCount,
        CancellationToken cancellationToken)
    {
        if (!RequiresProviderRouting(snapshot, publication))
        {
            return _fallbackReader;
        }

        var router = RequireProviderRouter(service, publication);
        var binding = await router.ResolveBindingAsync(
            snapshot,
            service!,
            resource,
            publication!,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);

        if (requireCount && !binding.Provider.Capabilities.SupportsCount)
        {
            throw new InvalidOperationException(
                $"Feature provider '{binding.Provider.ProviderName}' does not support count operations.");
        }

        return binding.Provider is IBindableFeatureDataProvider bindable
            ? bindable.CreateReaderForBinding(binding)
            : binding.Provider.Reader;
    }

    public async Task<(bool Supported, string? ErrorMessage)> CheckWriteSupportAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service? service,
        MetadataV2Resource resource,
        MetadataV2Publication? publication,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        if (!RequiresProviderRouting(snapshot, publication))
        {
            return (true, null);
        }

        var router = RequireProviderRouter(service, publication);
        var binding = await router.ResolveBindingAsync(
            snapshot,
            service!,
            resource,
            publication!,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);

        if (binding.Provider.Writer is null)
        {
            return (false, "The layer's configured data provider is read-only for OData write operations.");
        }

        return ReferenceEquals(binding.Provider.Writer, _fallbackWriter)
            ? (true, null)
            : (false, "OData writes through secondary data providers are not supported.");
    }

    private static bool RequiresProviderRouting(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication? publication)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (publication is null || string.IsNullOrWhiteSpace(publication.StorageBindingId))
        {
            return false;
        }

        // A publication with an explicit but invalid binding must still fail closed in the
        // router. Bindings without a connection describe the built-in provider's legacy
        // layer-id partition and must keep using the primary reader/writer pipeline.
        return !snapshot.Index.StorageBindingsById.TryGetValue(publication.StorageBindingId, out var binding)
            || !string.IsNullOrWhiteSpace(binding.ConnectionId);
    }

    private FeatureProviderQueryRouter RequireProviderRouter(
        MetadataV2Service? service,
        MetadataV2Publication? publication)
    {
        if (_providerQueryRouter is null)
        {
            throw new InvalidOperationException("Feature provider routing is not configured.");
        }

        if (service is null || publication is null)
        {
            throw new InvalidOperationException(
                "The authorized OData publication does not contain complete provider routing metadata.");
        }

        return _providerQueryRouter;
    }
}
