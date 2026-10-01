// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Infrastructure.Authentication;

internal sealed partial class RowLevelSecurityFilterSource
{
    public bool CanResolveWith(IFieldMaskSource fieldSource)
        => _httpContextAccessor.HttpContext is not null &&
           fieldSource is FieldMaskSource masks &&
           masks.HasSameContext(_httpContextAccessor, _graphProvider, _rbacOptions) &&
           _policyStore is ICombinedReadPolicyStore combined && combined.CanResolveWith(masks.PolicyStore);

    public async Task<ReadSecurityResolution> ResolveCombinedAsync(
        MetadataV2Resource resource, IFieldMaskSource fieldSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!CanResolveWith(fieldSource))
        {
            throw new ArgumentException("The registered row and field sources cannot resolve together.", nameof(fieldSource));
        }

        var principal = ResolvePrincipal();
        var layer = resource.Metadata.Name;
        if (principal is null || string.IsNullOrWhiteSpace(layer))
        {
            return new(null, ImmutableArray<string>.Empty);
        }
        var roles = RbacRoleClaims.Enumerate(principal, _rbacOptions,
            _httpContextAccessor.HttpContext?.RequestServices);
        var services = await ResolveServiceNamesAsync(resource, cancellationToken).ConfigureAwait(false);
        var policies = await ((ICombinedReadPolicyStore)_policyStore).GetEffectiveReadPoliciesAsync(
            ((FieldMaskSource)fieldSource).PolicyStore, roles, services, layer, cancellationToken).ConfigureAwait(false);
        SqlFragment? filter = null;
        foreach (var policy in policies.RowPolicies)
        {
            var (_, predicate) = BuildPredicate(policy, principal, resource);
            filter = filter is null ? predicate : AndFragments(filter, predicate);
        }
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in policies.FieldPolicies)
        {
            if (!string.IsNullOrWhiteSpace(policy.Attribute))
            {
                fields.Add(policy.Attribute.Trim());
            }
        }
        return new(filter, fields.ToImmutableArray());
    }
}
