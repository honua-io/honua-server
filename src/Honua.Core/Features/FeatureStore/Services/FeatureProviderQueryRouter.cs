// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Resolves provider-backed feature query operations from service and layer metadata.
/// </summary>
public sealed class FeatureProviderQueryRouter
{
    private readonly FeatureProviderBindingResolver _bindingResolver;
    private readonly ISecureConnectionRegistry? _connectionRegistry;
    private readonly IFeatureDataProviderRegistry? _providerRegistry;
    private readonly string _defaultProviderName;

    /// <summary>
    /// Creates a provider query router.
    /// </summary>
    /// <param name="bindingResolver">Provider binding resolver.</param>
    public FeatureProviderQueryRouter(FeatureProviderBindingResolver bindingResolver)
    {
        _bindingResolver = bindingResolver ?? throw new ArgumentNullException(nameof(bindingResolver));
        _defaultProviderName = DataProviderNames.Postgis;
    }

    /// <summary>
    /// Creates a provider query router that can resolve readers from either v1
    /// (<see cref="ServiceDefinition"/>/<see cref="LayerDefinition"/>) or V2
    /// (<see cref="MetadataV2Service"/>/<see cref="MetadataV2Publication"/>) inputs.
    /// </summary>
    /// <param name="bindingResolver">Provider binding resolver for v1 inputs.</param>
    /// <param name="connectionRegistry">Secure connection registry used to materialize
    /// V2 connection ids into <see cref="DataConnection"/> records.</param>
    /// <param name="providerRegistry">Feature provider registry used to look up
    /// providers by name on the V2 path.</param>
    /// <param name="defaultProviderName">Default provider used for V2 publications
    /// whose backing storage binding does not declare a connection.</param>
    public FeatureProviderQueryRouter(
        FeatureProviderBindingResolver bindingResolver,
        ISecureConnectionRegistry connectionRegistry,
        IFeatureDataProviderRegistry providerRegistry,
        string defaultProviderName = DataProviderNames.Postgis)
    {
        _bindingResolver = bindingResolver ?? throw new ArgumentNullException(nameof(bindingResolver));
        _connectionRegistry = connectionRegistry ?? throw new ArgumentNullException(nameof(connectionRegistry));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _defaultProviderName = DataProviderNames.Normalize(defaultProviderName);
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

    /// <summary>
    /// Resolves the feature reader for a V2 provider-backed read operation.
    /// </summary>
    /// <param name="snapshot">Current metadata V2 graph snapshot. Used to walk
    /// publication → storage binding → connection.</param>
    /// <param name="service">V2 service hosting the publication.</param>
    /// <param name="resource">Canonical V2 resource being queried.</param>
    /// <param name="publication">V2 publication on the service.</param>
    /// <param name="storageLayerId">Integer storage handle the resolved reader
    /// expects on its <c>QueryAsync(int layerId, …)</c> entry points. Carried so
    /// callers can forward through the v1 reader path unchanged.</param>
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
        _ = storageLayerId;

        if (_connectionRegistry == null || _providerRegistry == null)
        {
            throw new InvalidOperationException(
                "FeatureProviderQueryRouter was not constructed with the V2 overload dependencies; "
                + "the V2 ResolveReaderAsync overload is unavailable.");
        }

        var binding = snapshot.ResolveStorageBinding(publication)
            ?? throw new InvalidOperationException(
                $"Publication '{publication.Metadata.Id}' on service '{service.Metadata.Id}' does not resolve to a storage binding.");

        DataConnection? connection = null;
        var providerName = _defaultProviderName;

        var v2Connection = snapshot.ResolveConnection(binding);
        if (v2Connection != null)
        {
            connection = await _connectionRegistry
                .GetConnectionAsync(v2Connection.Metadata.Id, cancellationToken)
                .ConfigureAwait(false);

            if (connection == null)
            {
                throw new InvalidOperationException(
                    $"Connection '{v2Connection.Metadata.Id}' for publication '{publication.Metadata.Id}' on service '{service.Metadata.Id}' was not found.");
            }

            providerName = connection.NormalizedProvider;
        }
        else if (!string.IsNullOrEmpty(v2Connection?.Provider))
        {
            providerName = DataProviderNames.Normalize(v2Connection!.Provider!);
        }

        if (!_providerRegistry.TryGetProvider(providerName, out var provider))
        {
            throw new InvalidOperationException(
                $"Feature provider '{providerName}' is not registered for publication '{publication.Metadata.Id}' on service '{service.Metadata.Id}'.");
        }

        EnsureOperationSupported(provider, operation);

        // The V2 path forwards through the int-layer-id reader entry points
        // (see IFeatureReader.QueryAsync(int layerId, …)). Bindable providers
        // require a v1 FeatureProviderBinding to attach per-tenant connection
        // state — until a V2 bindable seam exists (cutover task #20), fall
        // back to the shared provider reader. This matches the documented
        // "v1 reader path forwarding" acceptance criterion for the cutover.
        return provider.Reader;
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
