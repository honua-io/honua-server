// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;

namespace Honua.Protocols.SensorThings;

internal static partial class SensorThingsEndpoints
{
    // Sensing entities carry no access policy of their own, so a read is admitted the way an
    // observation subscription is: a signed-in caller inside a resolved tenant scope. A deployment
    // that publishes its sensing data openly opts in explicitly.
    private static IResult? RequireReadAccess(HttpContext context)
    {
        if (context.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>(SensorThingsOptions.AllowAnonymousReadsPath))
        {
            return null;
        }

        var denied = AccessPolicyHelpers.RequireAccess(context, layerPolicy: null, servicePolicy: null, AccessScope.Read);
        if (denied is not null)
        {
            return denied;
        }

        return SensorThingsTenantScope.IsResolved(context)
            ? null
            : StandardErrorHelpers.CreateForbidden(context, SensorThingsTenantScope.UnresolvedMessage);
    }
}
