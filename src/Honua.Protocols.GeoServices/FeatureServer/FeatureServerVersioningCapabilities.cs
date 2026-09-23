// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    internal static (MetadataV2Publication Publication, MetadataV2Resource Resource)[] GetRoutableFeaturePublicationsV2(
        MetadataV2Service service, MetadataV2GraphSnapshot snapshot)
        => snapshot.Index.PublicationsByService[service.Metadata.Id]
            .Where(publication => ServiceProtocols.IsPreferredPublicationType(
                ServiceProtocols.FeatureServer, publication.PublicationType))
            .Where(snapshot.IsRoutable)
            .Select(publication => (publication, snapshot.ResolveResource(publication)!))
            .ToArray();

    internal static async Task<bool> HasAccessibleBranchVersionedPublicationsAsync(
        HttpContext context, MetadataV2Service service, MetadataV2GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var (publications, error) = await AccessPolicyHelpers.FilterAccessibleResourcesAsync(
            context, GetRoutableFeaturePublicationsV2(service, snapshot), static pair => pair.Resource,
            service, AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false);
        return error == null && await HasBranchVersionedPublicationsAsync(
            context, service, publications, snapshot, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasBranchVersionedPublicationsAsync(
        HttpContext context, MetadataV2Service service,
        IReadOnlyList<(MetadataV2Publication Publication, MetadataV2Resource Resource)> publications,
        MetadataV2GraphSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!IsBranchVersioningAvailable(context))
        {
            return false;
        }

        foreach (var (publication, resource) in publications)
        {
            if (await IsPublicationBranchVersioningAvailableAsync(
                context, service, resource, publication, snapshot, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> IsPublicationBranchVersioningAvailableAsync(
        HttpContext context, MetadataV2Service service, MetadataV2Resource resource,
        MetadataV2Publication publication, MetadataV2GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!IsBranchVersioningAvailable(context)
            || snapshot.ResolveStorageLayerId(publication) is not { } storageLayerId)
        {
            return false;
        }

        var router = context.RequestServices.GetService<FeatureProviderQueryRouter>();
        if (router == null)
        {
            return false;
        }

        try
        {
            // Use the exact publication -> binding -> secure connection -> provider route
            // used by queries. The bound reader owns both discovery and read eligibility.
            var reader = await router.ResolveReaderAsync(snapshot, service, resource, publication,
                storageLayerId, FeatureProviderReadOperation.Query, cancellationToken).ConfigureAwait(false);
            return reader is IBranchVersioningFeatureReader capability
                && await capability.SupportsBranchVersioningAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // An unresolved/unsupported publication must not inherit the host's capability.
            return false;
        }
    }
}
