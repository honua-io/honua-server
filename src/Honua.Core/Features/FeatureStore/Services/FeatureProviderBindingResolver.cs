// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Resolves the provider/runtime storage binding for a service layer.
/// </summary>
public sealed class FeatureProviderBindingResolver
{
    private readonly ISecureConnectionRegistry _connectionRegistry;
    private readonly IFeatureDataProviderRegistry _providerRegistry;
    private readonly string _defaultProviderName;

    /// <summary>
    /// Creates a provider binding resolver.
    /// </summary>
    /// <param name="connectionRegistry">Secure connection registry.</param>
    /// <param name="providerRegistry">Feature provider registry.</param>
    /// <param name="defaultProviderName">Default provider used for services without a secure connection.</param>
    public FeatureProviderBindingResolver(
        ISecureConnectionRegistry connectionRegistry,
        IFeatureDataProviderRegistry providerRegistry,
        string defaultProviderName = DataProviderNames.Postgis)
    {
        _connectionRegistry = connectionRegistry ?? throw new ArgumentNullException(nameof(connectionRegistry));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _defaultProviderName = DataProviderNames.Normalize(defaultProviderName);
    }

    /// <summary>
    /// Resolves a service/layer pair to its feature provider and physical storage mapping.
    /// </summary>
    /// <param name="service">Service containing the layer.</param>
    /// <param name="layer">Layer to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved provider binding.</returns>
    /// <exception cref="InvalidOperationException">Thrown when required binding inputs are missing or unsupported.</exception>
    public async Task<FeatureProviderBinding> ResolveAsync(
        ServiceDefinition service,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(layer);

        var storageMapping = layer.StorageMapping
            ?? throw new InvalidOperationException(
                $"Layer '{layer.Name}' does not define a runtime storage mapping.");

        var storageErrors = storageMapping.Validate();
        if (storageErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Layer '{layer.Name}' has an invalid runtime storage mapping: {string.Join("; ", storageErrors)}");
        }

        DataConnection? connection = null;
        var providerName = _defaultProviderName;

        if (service.ConnectionId.HasValue)
        {
            connection = await _connectionRegistry
                .GetConnectionAsync(service.ConnectionId.Value, cancellationToken)
                .ConfigureAwait(false);

            if (connection == null)
            {
                throw new InvalidOperationException(
                    $"Connection '{service.ConnectionId.Value}' for service '{service.Name}' was not found.");
            }

            providerName = connection.NormalizedProvider;
        }

        if (!_providerRegistry.TryGetProvider(providerName, out var provider))
        {
            throw new InvalidOperationException(
                $"Feature provider '{providerName}' is not registered for service '{service.Name}'.");
        }

        return new FeatureProviderBinding(service, layer, storageMapping, provider, connection);
    }
}

/// <summary>
/// Runtime binding for a service layer, secure connection, storage mapping, and provider.
/// </summary>
/// <param name="Service">Service definition being resolved.</param>
/// <param name="Layer">Layer definition being resolved.</param>
/// <param name="StorageMapping">Physical storage mapping for the layer.</param>
/// <param name="Provider">Resolved provider implementation.</param>
/// <param name="Connection">Secure connection used to select the provider, when any.</param>
public sealed record FeatureProviderBinding(
    ServiceDefinition Service,
    LayerDefinition Layer,
    LayerStorageMapping StorageMapping,
    IFeatureDataProvider Provider,
    DataConnection? Connection);
