// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Composed access-policy inputs for a (resource, service) pair. Produced by
/// <see cref="MetadataV2AccessPolicyResolver.Resolve(MetadataV2Resource?, MetadataV2Service?)"/>;
/// consumed by <c>IAccessPolicyEvaluator</c>.
/// </summary>
/// <remarks>
/// This is a materialised view of the two-layer composition the v2 access model uses:
/// <list type="bullet">
/// <item>Resource-level policy on <see cref="MetadataV2Resource.AccessPolicy"/></item>
/// <item>Service-level policy on <see cref="MetadataV2Service.AccessPolicy"/></item>
/// </list>
/// The composition rule is deny-wins — both must pass. Returning the two policies
/// distinct (rather than pre-merging into a single composite) preserves the
/// evaluator's ability to surface scope-specific reasons (e.g. "resource X is
/// admin-only" vs. "service Y requires auth") in the resulting
/// <c>AccessDecision.Reason</c>.
/// </remarks>
public readonly record struct MetadataV2EffectiveAccessPolicy(
    AccessPolicy? Resource,
    AccessPolicy? Service);

/// <summary>
/// Resolves the composed access-policy inputs for a (resource, service) pair.
/// </summary>
public static class MetadataV2AccessPolicyResolver
{
    /// <summary>
    /// Materialises the composed effective policy for a resource accessed through
    /// a particular service. Either argument may be <c>null</c>; the returned
    /// struct simply carries whichever policies are set.
    /// </summary>
    public static MetadataV2EffectiveAccessPolicy Resolve(
        MetadataV2Resource? resource,
        MetadataV2Service? service)
        => new(resource?.AccessPolicy, service?.AccessPolicy);
}
