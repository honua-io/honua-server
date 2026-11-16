// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

public sealed class FeatureProviderQueryRouter
{
    // PA-019: every query goes through this resolution hot path, and it previously emitted no
    // telemetry at all (no span, no metrics, no logging).
    private static readonly ActivitySource _activitySource = new("Honua.Core.FeatureStore");

    private readonly ISecureConnectionRegistry _connectionRegistry;
    private readonly IFeatureDataProviderRegistry _providerRegistry;
    private readonly string _defaultProviderName;

    public FeatureProviderQueryRouter(
        ISecureConnectionRegistry connectionRegistry,
        IFeatureDataProviderRegistry providerRegistry,
        string defaultProviderName = DataProviderNames.Postgis)
    {
        _connectionRegistry = connectionRegistry ?? throw new ArgumentNullException(nameof(connectionRegistry));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _defaultProviderName = DataProviderNames.Normalize(defaultProviderName);
    }

    public async Task<IFeatureReader> ResolveReaderAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        MetadataV2Object resource,
        MetadataV2Layer publication,
        int storageLayerId,
        FeatureProviderReadOperation operation,
        CancellationToken cancellationToken = default)
    {
        var providerBinding = await ResolveBindingAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId,
            operation,
            cancellationToken).ConfigureAwait(false);

        return providerBinding.Provider is IBindableFeatureDataProvider bindable
            ? bindable.CreateReaderForBinding(providerBinding)
            : providerBinding.Provider.Reader;
    }

    public async Task<FeatureProviderBinding> ResolveBindingAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        MetadataV2Object resource,
        MetadataV2Layer publication,
        int storageLayerId,
        FeatureProviderReadOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(publication);

        using var activity = _activitySource.StartActivity("featurestore.resolve_reader");
        activity?.SetTag("service.id", service.Metadata.Id);
        activity?.SetTag("resource.id", resource.Metadata.Id);
        activity?.SetTag("publication.id", publication.Metadata.Id);

        try
        {
            var storageBinding = ResolveAuthorizedStorageBinding(snapshot, resource, publication);
            var resolvedStorageLayerId = storageBinding.StorageLayerId ?? storageLayerId;
            var storageMapping = FeatureStorageMapping.FromMetadata(resource, storageBinding);

            var (connection, providerName, missingConnectionId) = await ResolveProviderNameAsync(
                snapshot,
                storageBinding,
                cancellationToken).ConfigureAwait(false);
            if (providerName is null)
            {
                throw new InvalidOperationException(
                    $"Connection '{missingConnectionId}' for publication '{publication.Metadata.Id}' on service '{service.Metadata.Id}' was not found.");
            }

            if (!_providerRegistry.TryGetProvider(providerName, out var provider))
            {
                throw new InvalidOperationException(
                    $"Feature provider '{providerName}' is not registered for publication '{publication.Metadata.Id}' on service '{service.Metadata.Id}'.");
            }

            activity?.SetTag("provider.name", providerName);
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

            return providerBinding;
        }
        catch (InvalidOperationException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async Task<IFeatureDataProvider?> TryResolveProviderAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2StorageBinding? storageBinding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var providerName = _defaultProviderName;
        if (storageBinding is not null)
        {
            var resolved = await ResolveProviderNameAsync(snapshot, storageBinding, cancellationToken).ConfigureAwait(false);
            if (resolved.ProviderName is null)
            {
                return null;
            }

            providerName = resolved.ProviderName;
        }

        return _providerRegistry.TryGetProvider(providerName, out var provider) ? provider : null;
    }

    private static MetadataV2StorageBinding ResolveAuthorizedStorageBinding(
        MetadataV2GraphSnapshot snapshot, MetadataV2Object resource, MetadataV2Layer publication)
    {
        if (publication.ResourceId != resource.Metadata.Id ||
            (!string.IsNullOrWhiteSpace(publication.StorageBindingId) &&
             !snapshot.Index.StorageBindingsById.ContainsKey(publication.StorageBindingId)))
        {
            throw new InvalidOperationException("The authorized publication references an invalid storage binding.");
        }
        var binding = snapshot.ResolveStorageBinding(publication)
            ?? throw new InvalidOperationException("The authorized publication does not resolve to a storage binding.");
        if (binding.ResourceId != resource.Metadata.Id)
        {
            throw new InvalidOperationException("The authorized publication references an invalid storage binding.");
        }
        return binding;
    }

    private async Task<(DataConnection? Connection, string? ProviderName, string? MissingConnectionId)> ResolveProviderNameAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2StorageBinding storageBinding,
        CancellationToken cancellationToken)
    {
        var v2Connection = snapshot.ResolveConnection(storageBinding);
        if (v2Connection == null)
        {
            return (null, _defaultProviderName, null);
        }

        var connection = await _connectionRegistry
            .GetConnectionAsync(v2Connection.Metadata.Id, cancellationToken)
            .ConfigureAwait(false);
        if (connection != null)
        {
            return (connection, connection.NormalizedProvider, null);
        }

        return string.IsNullOrWhiteSpace(v2Connection.Provider)
            ? (null, null, v2Connection.Metadata.Id)
            : (null, DataProviderNames.Normalize(v2Connection.Provider!), null);
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
