// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// A layer targeted by a read policy whose serving provider does not advertise
/// <see cref="Domain.FeatureProviderCapabilities.SupportsReadPolicyEnforcement"/>.
/// </summary>
/// <param name="LayerName">Name of the targeted layer (metadata resource).</param>
/// <param name="ProviderName">Name of the provider that serves the layer.</param>
public sealed record UnenforceableReadPolicyTarget(string LayerName, string ProviderName);

/// <summary>
/// Decides whether a row-level security or field-mask policy scoped to a service / layer pair
/// can be enforced by every feature provider that serves the layers it targets. Policy
/// administration uses it to refuse a policy that would otherwise only surface as refused
/// reads on providers that cannot apply it.
/// </summary>
/// <remarks>
/// Targeting mirrors how the request-scoped policy sources match a policy to a resource: the
/// layer scope is compared (case-insensitively) with the resource name and the service scope
/// with the name of any service that publishes the resource; <c>*</c> matches everything.
/// </remarks>
public sealed class ReadPolicyEnforceabilityChecker
{
    private const string Wildcard = "*";

    private readonly IMetadataV2GraphProvider? _graphProvider;
    private readonly FeatureProviderQueryRouter? _router;
    private readonly IFeatureDataProviderRegistry? _providerRegistry;

    /// <summary>
    /// Creates a checker over the optional metadata graph, provider router and provider registry.
    /// </summary>
    /// <param name="graphProvider">Metadata v2 graph provider used to find the targeted layers.</param>
    /// <param name="router">Provider router used to resolve the provider that serves a storage binding.</param>
    /// <param name="providerRegistry">Registered feature providers, used when the targeted layers
    /// cannot be resolved from metadata.</param>
    public ReadPolicyEnforceabilityChecker(
        IMetadataV2GraphProvider? graphProvider,
        FeatureProviderQueryRouter? router,
        IFeatureDataProviderRegistry? providerRegistry)
    {
        _graphProvider = graphProvider;
        _router = router;
        _providerRegistry = providerRegistry;
    }

    /// <summary>
    /// Returns the first layer targeted by a policy scoped to <paramref name="service"/> /
    /// <paramref name="layer"/> that is served by a provider which cannot enforce read policies,
    /// or <see langword="null"/> when every targeted layer is enforceable (including when the
    /// scope currently targets no layer).
    /// </summary>
    /// <param name="service">Policy service scope (a service name, or <c>*</c>).</param>
    /// <param name="layer">Policy layer scope (a layer name, or <c>*</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first unenforceable target, or <see langword="null"/>.</returns>
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

            var bindings = snapshot.Index.StorageBindingsByResource[resource.Metadata.Id].ToArray();

            // A resource without a storage binding is read through the default provider.
            var candidates = bindings.Length == 0
                ? new MetadataV2StorageBinding?[] { null }
                : bindings.Cast<MetadataV2StorageBinding?>();

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

    private static bool IsTargeted(MetadataV2GraphSnapshot snapshot, MetadataV2Resource resource, string service, string layer)
    {
        if (string.IsNullOrWhiteSpace(resource.Metadata.Name))
        {
            return false;
        }

        if (layer != Wildcard && !string.Equals(layer, resource.Metadata.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (service == Wildcard)
        {
            return true;
        }

        foreach (var publication in snapshot.Index.PublicationsByResource[resource.Metadata.Id])
        {
            if (snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var publishingService) &&
                string.Equals(publishingService.Metadata.Name, service, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
