// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Access decisions for a fixed set of resources, resolved once per request through the
/// canonical per-operation seam (permission grants first, then the coarse access policy).
/// Synchronous layer selection (render, identify, replication) consults this set so it applies
/// the same rule as the async handlers: a caller the catalog lists a service to is not refused
/// by the operations that follow the handoff (honua-server#4783). A resource outside the set
/// falls back to the coarse evaluation.
/// </summary>
internal sealed class ResourceAccessSet
{
    private readonly HttpContext _context;
    private readonly MetadataV2Service? _service;
    private readonly AccessScope _fallbackScope;
    private readonly Dictionary<MetadataV2Resource, AccessDecision> _decisions;

    internal ResourceAccessSet(
        HttpContext context,
        MetadataV2Service? service,
        AccessScope fallbackScope,
        Dictionary<MetadataV2Resource, AccessDecision> decisions)
    {
        _context = context;
        _service = service;
        _fallbackScope = fallbackScope;
        _decisions = decisions;
    }

    /// <summary>Returns the resolved decision for <paramref name="resource"/>.</summary>
    public AccessDecision Evaluate(MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _decisions.TryGetValue(resource, out var decision)
            ? decision
            : AccessPolicyHelpers.EvaluateResourceAccess(_context, resource, _service, _fallbackScope);
    }

    /// <summary>Returns whether the caller may access <paramref name="resource"/>.</summary>
    public bool IsAccessible(MetadataV2Resource resource) => Evaluate(resource).IsAllowed;

    /// <summary>Returns the denial result for <paramref name="resource"/>, or <see langword="null"/> when allowed.</summary>
    public IResult? RequireAccess(MetadataV2Resource resource)
        => AccessPolicyHelpers.CreateAccessDeniedResult(_context, Evaluate(resource));

    /// <summary>
    /// Returns <see langword="null"/> when any resource is allowed (or none is supplied);
    /// otherwise the challenge when any denial requires authentication, else forbidden.
    /// </summary>
    public IResult? RequireAny(IEnumerable<MetadataV2Resource> resources)
        => AccessPolicyHelpers.RequireAnyDecision(_context, resources, Evaluate);
}
