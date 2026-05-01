// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Resolves provider-backed feature query operations from service and layer metadata.
/// </summary>
public sealed class FeatureProviderQueryRouter
{
    private readonly FeatureProviderBindingResolver _bindingResolver;

    /// <summary>
    /// Creates a provider query router.
    /// </summary>
    /// <param name="bindingResolver">Provider binding resolver.</param>
    public FeatureProviderQueryRouter(FeatureProviderBindingResolver bindingResolver)
    {
        _bindingResolver = bindingResolver ?? throw new ArgumentNullException(nameof(bindingResolver));
    }

    /// <summary>
    /// Resolves the feature reader for a provider-backed read operation.
    /// </summary>
    /// <param name="service">Service definition containing the layer.</param>
    /// <param name="layer">Layer definition to query.</param>
    /// <param name="operation">Read operation being executed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved feature reader.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the provider does not support the operation.</exception>
    public async Task<IFeatureReader> ResolveReaderAsync(
        ServiceDefinition service,
        LayerDefinition layer,
        FeatureProviderReadOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(layer);

        var binding = await _bindingResolver
            .ResolveAsync(service, layer, cancellationToken)
            .ConfigureAwait(false);

        EnsureOperationSupported(binding.Provider, operation);
        return binding.Provider is IBindableFeatureDataProvider bindable
            ? bindable.CreateReaderForBinding(binding)
            : binding.Provider.Reader;
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
