// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed class PublicPublicationEligibility(
    IAccessPolicyEvaluator access,
    IRlsPolicyStore rows,
    IFieldMaskPolicyStore masks)
{
    // Called only while the publication session holds the access-write fence.
    public async Task<bool> IsEligibleAsync(MetadataV2GraphSnapshot snapshot,
        PMTilesPublishedSource source, CancellationToken cancellationToken)
    {
        if (!MetadataV2TenantVisibility.IsVisibleToTenant(source.Publication, source.Resource, source.Service, null)
            || !string.IsNullOrWhiteSpace(source.Resource.PermanentFilter?.Expression)
            || !access.Evaluate(new ClaimsPrincipal(new ClaimsIdentity()), source.Resource.AccessPolicy,
                source.Service.AccessPolicy, AccessScope.Read).IsAllowed)
        {
            return false;
        }

        var serviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source.Service.Metadata.Name };
        if (snapshot.Index.ServiceNamesByResource.TryGetValue(source.Resource.Metadata.Id, out var aliases))
        {
            serviceNames.UnionWith(aliases);
        }

        var layer = source.Resource.Metadata.Name;
        var rowPolicies = await rows.ListPoliciesAsync(cancellationToken).ConfigureAwait(false);
        if (rowPolicies.Any(policy => MatchesService(policy.Service, serviceNames) && MatchesLayer(policy.Layer, layer)))
        {
            return false;
        }

        var maskPolicies = await masks.ListPoliciesAsync(cancellationToken).ConfigureAwait(false);
        return !maskPolicies.Any(policy => MatchesService(policy.Service, serviceNames) && MatchesLayer(policy.Layer, layer));
    }

    private static bool MatchesService(string policy, HashSet<string> services) => policy == "*" || services.Contains(policy);

    private static bool MatchesLayer(string policy, string layer) => policy == "*" || string.Equals(policy, layer, StringComparison.OrdinalIgnoreCase);
}
