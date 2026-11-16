// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.FeatureStore.Services;

public sealed record UnenforceableReadPolicyTarget(string LayerName, string ProviderName);

public sealed class ReadPolicyEnforceabilityChecker
{
    private const string Wildcard = "*";

    private readonly IMetadataV2GraphProvider? _graphProvider;
    private readonly FeatureProviderQueryRouter? _router;
    private readonly IFeatureDataProviderRegistry? _providerRegistry;

    public ReadPolicyEnforceabilityChecker(
        IMetadataV2GraphProvider? graphProvider,
        FeatureProviderQueryRouter? router,
        IFeatureDataProviderRegistry? providerRegistry)
    {
        _graphProvider = graphProvider;
        _router = router;
        _providerRegistry = providerRegistry;
    }

    public async Task<UnenforceableReadPolicyTarget?> FindUnenforceableTargetAsync(
        string service,
        string layer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(layer);

        if (_graphProvider is null || _router is null)
        {
            // Without metadata the targeted layers cannot be resolved, so the policy is only
            // enforceable when every registered provider can enforce it.
            var unsupported = _providerRegistry?.Providers
                .FirstOrDefault(static provider => !provider.Capabilities.SupportsReadPolicyEnforcement);
            return unsupported is null ? null : new UnenforceableReadPolicyTarget(layer, unsupported.ProviderName);
        }

        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        foreach (var resource in snapshot.Index.ResourcesById.Values)
        {
            if (!IsTargeted(snapshot, resource, service, layer))
            {
                continue;
            }

            var bindingsById = new Dictionary<string, MetadataV2StorageBinding>(StringComparer.Ordinal);

            // Direct resource reads use the resource's primary binding. Do not scan every
            // alternative binding: publication routing may explicitly select only one of them.
            if (!string.IsNullOrWhiteSpace(resource.PrimaryStorageBindingId) &&
                snapshot.Index.StorageBindingsById.TryGetValue(resource.PrimaryStorageBindingId, out var primaryBinding))
            {
                bindingsById.TryAdd(primaryBinding.Metadata.Id, primaryBinding);
            }

            // Service-scoped reads use the binding selected by each targeted publication
            // (explicit publication override, then primary, then first fallback).
            foreach (var publication in snapshot.Index.PublicationsByResource[resource.Metadata.Id])
            {
                if (!IsTargetedService(snapshot, publication, service))
                {
                    continue;
                }

                var binding = snapshot.ResolveStorageBinding(publication);
                if (binding is not null)
                {
                    bindingsById.TryAdd(binding.Metadata.Id, binding);
                }
            }

            // A resource without a usable binding is read through the default provider.
            var candidates = bindingsById.Count == 0
                ? new MetadataV2StorageBinding?[] { null }
                : bindingsById.Values.Cast<MetadataV2StorageBinding?>();

            foreach (var binding in candidates)
            {
                // A binding whose connection or provider cannot be resolved is skipped: the
                // router refuses every read of it, so there is nothing a policy could miss.
                var provider = await _router.TryResolveProviderAsync(snapshot, binding, cancellationToken).ConfigureAwait(false);
                if (provider is not null && !provider.Capabilities.SupportsReadPolicyEnforcement)
                {
                    return new UnenforceableReadPolicyTarget(resource.Metadata.Name, provider.ProviderName);
                }
            }
        }

        return null;
    }

    private static bool IsTargeted(MetadataV2GraphSnapshot snapshot, MetadataV2Object resource, string service, string layer)
    {
        if (string.IsNullOrWhiteSpace(resource.Metadata.Name))
        {
            return false;
        }

        if (layer != Wildcard && !string.Equals(layer, resource.Metadata.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return service == Wildcard || snapshot.Index.PublicationsByResource[resource.Metadata.Id]
            .Any(publication => IsTargetedService(snapshot, publication, service));
    }

    private static bool IsTargetedService(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Layer publication,
        string service)
        => service == Wildcard ||
           (snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var publishingService) &&
            string.Equals(publishingService.Metadata.Name, service, StringComparison.OrdinalIgnoreCase));
}
