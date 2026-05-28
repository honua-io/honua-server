// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Resolves provider-backed feature query operations from Metadata v2 service and publication metadata.
/// </summary>
public sealed class FeatureProviderQueryRouter
{
    private readonly ISecureConnectionRegistry _connectionRegistry;
    private readonly IFeatureDataProviderRegistry _providerRegistry;
    private readonly string _defaultProviderName;

    /// <summary>
    /// Creates a provider query router that resolves readers from Metadata v2
    /// service/publication/storage-binding inputs.
    /// </summary>
    /// <param name="connectionRegistry">Secure connection registry used to materialize
    /// Metadata v2 connection ids into <see cref="DataConnection"/> records.</param>
    /// <param name="providerRegistry">Feature provider registry used to look up
    /// providers by name.</param>
    /// <param name="defaultProviderName">Default provider used for V2 publications
    /// whose backing storage binding does not declare a connection.</param>
    public FeatureProviderQueryRouter(
        ISecureConnectionRegistry connectionRegistry,
        IFeatureDataProviderRegistry providerRegistry,
        string defaultProviderName = DataProviderNames.Postgis)
    {
        _connectionRegistry = connectionRegistry ?? throw new ArgumentNullException(nameof(connectionRegistry));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _defaultProviderName = DataProviderNames.Normalize(defaultProviderName);
    }

    /// <summary>
    /// Resolves the feature reader for a V2 provider-backed read operation.
    /// </summary>
    /// <param name="snapshot">Current metadata V2 graph snapshot. Used to walk
    /// publication → storage binding → connection.</param>
    /// <param name="service">V2 service hosting the publication.</param>
    /// <param name="resource">Canonical V2 resource being queried.</param>
    /// <param name="publication">V2 publication on the service.</param>
    /// <param name="storageLayerId">Integer storage handle the resolved reader
    /// expects on its <c>QueryAsync(int layerId, …)</c> entry points.</param>
    /// <param name="operation">Read operation being executed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved feature reader.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the V2 graph is
    /// misconfigured, the connection is missing, the provider is unregistered, or
    /// the provider does not support the operation.</exception>
    public async Task<IFeatureReader> ResolveReaderAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureProviderReadOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(publication);
        var storageBinding = snapshot.ResolveStorageBinding(publication)
            ?? throw new InvalidOperationException(
                $"Publication '{publication.Metadata.Id}' on service '{service.Metadata.Id}' does not resolve to a storage binding.");
        var resolvedStorageLayerId = storageBinding.StorageLayerId ?? storageLayerId;
        var storageMapping = FeatureStorageMapping.FromMetadata(resource, storageBinding);

        DataConnection? connection = null;
        var providerName = _defaultProviderName;

        var v2Connection = snapshot.ResolveConnection(storageBinding);
        if (v2Connection != null)
        {
            connection = await _connectionRegistry
                .GetConnectionAsync(v2Connection.Metadata.Id, cancellationToken)
                .ConfigureAwait(false);

            if (connection != null)
            {
                providerName = connection.NormalizedProvider;
            }
            else if (!string.IsNullOrWhiteSpace(v2Connection.Provider))
            {
                providerName = DataProviderNames.Normalize(v2Connection.Provider!);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Connection '{v2Connection.Metadata.Id}' for publication '{publication.Metadata.Id}' on service '{service.Metadata.Id}' was not found.");
            }
        }

        if (!_providerRegistry.TryGetProvider(providerName, out var provider))
        {
            throw new InvalidOperationException(
                $"Feature provider '{providerName}' is not registered for publication '{publication.Metadata.Id}' on service '{service.Metadata.Id}'.");
        }

        EnsureOperationSupported(provider, operation);

        var providerBinding = new FeatureProviderBinding(
            service,
            resource,
            publication,
            storageBinding,
            storageMapping,
            resolvedStorageLayerId,
            provider,
            connection);

        return provider is IBindableFeatureDataProvider bindable
            ? bindable.CreateReaderForBinding(providerBinding)
            : provider.Reader;
    }

    private static void EnsureOperationSupported(
        IFeatureDataProvider provider,
        FeatureProviderReadOperation operation)
    {
        var capabilities = provider.Capabilities;
        var supported = operation switch
        {
            FeatureProviderReadOperation.Query => capabilities.SupportsQuery,
            FeatureProviderReadOperation.Count => capabilities.SupportsCount,
            FeatureProviderReadOperation.Extent => capabilities.SupportsExtent,
            FeatureProviderReadOperation.Statistics => capabilities.SupportsStatistics,
            _ => false
        };

        if (!supported)
        {
            throw new InvalidOperationException(
                $"Feature provider '{provider.ProviderName}' does not support {operation.ToString().ToLowerInvariant()} operations.");
        }
    }
}
