// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Infrastructure.Authentication;

internal sealed class ResourceAccessSet
{
    private readonly HttpContext _context;
    private readonly Dictionary<MetadataV2Object, AccessDecision> _decisions;

    internal ResourceAccessSet(
        HttpContext context,
        MetadataV2Service? service,
        AccessScope fallbackScope,
        Dictionary<MetadataV2Object, AccessDecision> decisions)
    {
        _ = service;
        _ = fallbackScope;
        _context = context;
        _decisions = decisions;
    }

    public AccessDecision Evaluate(MetadataV2Object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _decisions.TryGetValue(resource, out var decision)
            ? decision
            : throw new InvalidOperationException(
                $"Resource '{resource.Metadata.Id}' was not pre-evaluated in this ResourceAccessSet.");
    }

    public bool IsAccessible(MetadataV2Object resource) => Evaluate(resource).IsAllowed;

    public IResult? RequireAccess(MetadataV2Object resource)
        => AccessPolicyHelpers.CreateAccessDeniedResult(_context, Evaluate(resource));

    public IResult? RequireAny(IEnumerable<MetadataV2Object> resources)
        => AccessPolicyHelpers.RequireAnyDecision(_context, resources, Evaluate);
}
