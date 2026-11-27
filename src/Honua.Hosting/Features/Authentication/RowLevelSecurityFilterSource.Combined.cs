// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Infrastructure.Authentication;

internal sealed partial class RowLevelSecurityFilterSource
{
    public bool CanResolveWith(IFieldMaskSource fieldSource)
        => _httpContextAccessor.HttpContext is not null
           && fieldSource is FieldMaskSource masks
           && masks.HasSameContext(_httpContextAccessor, _graphProvider, _rbacOptions, _backgroundServices)
           && _policyStore is ICombinedReadPolicyStore combined
           && combined.CanResolveWith(masks.PolicyStore);

    public async Task<ReadSecurityResolution> ResolveCombinedAsync(
        MetadataV2Object resource,
        IFieldMaskSource fieldSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanResolveWith(fieldSource))
        {
            throw new ArgumentException(
                "The registered row and field sources cannot resolve together.",
                nameof(fieldSource));
        }

        var principal = ResolvePrincipal();
        var layer = resource.Metadata.Name;
        if (principal is null || string.IsNullOrWhiteSpace(layer))
        {
            return new ReadSecurityResolution(null, ImmutableArray<string>.Empty);
        }

        var roles = RbacRoleClaims.Enumerate(
            principal,
            _rbacOptions,
            _httpContextAccessor.HttpContext?.RequestServices ?? _backgroundServices);
        var services = await ResolveServiceNamesAsync(resource, cancellationToken).ConfigureAwait(false);
        var policies = await ((ICombinedReadPolicyStore)_policyStore).GetEffectiveReadPoliciesAsync(
            ((FieldMaskSource)fieldSource).PolicyStore,
            roles,
            services,
            layer,
            cancellationToken).ConfigureAwait(false);
        var fallbackMode = IsFullAdmin(principal)
            ? UnmatchedPolicyFallback.AdminBypass
            : UnmatchedPolicyFallback.FailClosed;
        var filter = ResolveCombinedRowFilter(policies, new CombinedRowResolutionContext(principal, resource, fallbackMode));
        var masked = ResolveCombinedMaskedFields(policies, fallbackMode);
        return new ReadSecurityResolution(filter, masked);
    }

    private enum UnmatchedPolicyFallback
    {
        FailClosed,
        AdminBypass,
    }

    private readonly record struct CombinedRowResolutionContext(
        ClaimsPrincipal Principal,
        MetadataV2Object Resource,
        UnmatchedPolicyFallback Fallback);

    private SqlFragment? ResolveCombinedRowFilter(
        EffectiveReadPolicies policies,
        CombinedRowResolutionContext context)
    {
        if (policies.RowPolicies.Count > 0)
        {
            SqlFragment? filter = null;
            foreach (var policy in policies.RowPolicies)
            {
                var (_, predicate) = BuildPredicate(policy, context.Principal, context.Resource);
                filter = filter is null ? predicate : AndFragments(filter, predicate);
            }

            return filter;
        }

        return context.Fallback == UnmatchedPolicyFallback.FailClosed && policies.HasAnyLayerRowPolicies
            ? BuildFailClosedPredicate().Sql
            : null;
    }

    private static ImmutableArray<string> ResolveCombinedMaskedFields(
        EffectiveReadPolicies policies,
        UnmatchedPolicyFallback fallback)
    {
        var active = policies.FieldPolicies.Count > 0
            ? policies.FieldPolicies
            : fallback == UnmatchedPolicyFallback.FailClosed
                ? policies.AllLayerFieldPolicies
                : null;
        if (active is null || active.Count == 0)
        {
            return ImmutableArray<string>.Empty;
        }

        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in active.Where(static policy => !string.IsNullOrWhiteSpace(policy.Attribute)))
        {
            fields.Add(policy.Attribute.Trim());
        }

        return fields.Count == 0 ? ImmutableArray<string>.Empty : fields.ToImmutableArray();
    }
}
