// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Validation;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;
using AccessPolicy = Honua.Core.Features.Security.Domain.AccessPolicy;

namespace Honua.Infrastructure.Authentication;

// The read decision a catalog applies before it lists or describes a scene, so it never shows a
// caller a scene whose assets the same caller is refused.
internal static class SceneReadAccess
{
    public static AccessDecision Evaluate(HttpContext context, AccessPolicy accessPolicy)
        => AccessPolicyHelpers.EvaluateAccess(
            context,
            layerPolicy: accessPolicy,
            servicePolicy: null,
            scope: AccessScope.Read);

    // A scene with no access policy is public.
    public static bool IsReadable(HttpContext context, AccessPolicy? accessPolicy)
        => accessPolicy is null || Evaluate(context, accessPolicy).IsAllowed;
}
