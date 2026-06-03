// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Styling;

/// <summary>
/// Emits advisory deprecation signaling for the legacy layerId-keyed style
/// aliases. The canonical style identifier is <c>styleId</c> via
/// <c>/ogc/styles/{styleId}</c> (ADR-0048); the layerId-keyed paths
/// (<c>/api/styles/{layerId}.json</c> and the admin
/// <c>…/layers/{layerId}/style</c> endpoints) remain working back-compat
/// aliases until consumers migrate. Per ADR-0048 the aliases are retired only
/// after the SDK/console/MCP styleId rollout completes (Phase 3); removing them
/// now would break clients before they migrate.
/// </summary>
internal static class StyleDeprecationHeaders
{
    /// <summary>
    /// The RFC 8594 <c>Deprecation</c> header. The presence/<c>true</c> value
    /// signals the resource is deprecated.
    /// </summary>
    public const string DeprecationHeader = "Deprecation";

    /// <summary>
    /// The RFC 8594 <c>Sunset</c> header advertising when the alias may stop
    /// responding. The exact date is gated on downstream styleId adoption, so
    /// the value is intentionally advisory rather than a hard commitment.
    /// </summary>
    public const string SunsetHeader = "Sunset";

    /// <summary>
    /// The <c>Link</c> header pointing clients at the canonical replacement.
    /// </summary>
    public const string LinkHeader = "Link";

    /// <summary>
    /// Adds the advisory deprecation response headers for a layerId-keyed style
    /// alias. Behavior is otherwise unchanged; this only signals deprecation.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="successorRelativePath">
    /// The canonical successor path (e.g. <c>/ogc/styles</c>) advertised to the
    /// client via the <c>Link: rel="successor-version"</c> relation.
    /// </param>
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
