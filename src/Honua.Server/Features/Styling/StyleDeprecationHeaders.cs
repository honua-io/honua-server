// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Styling;

// The layer-id style aliases stay, because removing them would break clients that still call them.
internal static class StyleDeprecationHeaders
{
    public const string DeprecationHeader = "Deprecation";

    public const string SunsetHeader = "Sunset";

    public const string LinkHeader = "Link";

    public static void Apply(HttpContext context, string successorRelativePath)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headers = context.Response.Headers;
        headers[DeprecationHeader] = "true";

        // Advisory sunset: the layerId aliases are retired only after the
        // cross-repo styleId rollout (ADR-0048 Phase 3). The date is a
        // non-binding signal that the alias is on a deprecation track and may
        // move once downstream consumers have migrated to styleId.
        headers[SunsetHeader] = "Thu, 31 Dec 2026 23:59:59 GMT";

        headers.Append(LinkHeader, $"<{successorRelativePath}>; rel=\"successor-version\"");
    }
}
