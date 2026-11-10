// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Infrastructure.SharedCaching;

// Proof for the cache policy that the access pipeline granted this request to an anonymous caller.
internal static class ResponseAccessVerdict
{
    private static readonly object VerdictKey = new();

    private enum Verdict
    {
        AnonymousGranted,
        Restricted,
    }

    internal static void Record(HttpContext context, AccessDecision decision)
    {
        if (!decision.IsAllowed || !RequestAnonymity.IsAnonymous(context))
        {
            context.Items[VerdictKey] = Verdict.Restricted;
            return;
        }

        context.Items.TryAdd(VerdictKey, Verdict.AnonymousGranted);
    }

    // Handlers that filter by access evaluate without refusing. A grant seen there still proves an anonymous caller may read
    // what is served; a denial there only removes content from the answer, so it is not a refusal of the request.
    internal static AccessDecision ObserveGrant(HttpContext context, AccessDecision decision)
    {
        if (decision.IsAllowed)
        {
            Record(context, decision);
        }

        return decision;
    }

    internal static bool GrantedToAnonymous(HttpContext context)
        => context.Items.TryGetValue(VerdictKey, out var verdict) && verdict is Verdict.AnonymousGranted;

    internal static bool Restricted(HttpContext context)
        => context.Items.TryGetValue(VerdictKey, out var verdict) && verdict is Verdict.Restricted;
}
