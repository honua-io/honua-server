// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Resolves the <c>href</c> of a GeoServices image envelope (MapServer <c>export?f=json</c>,
/// ImageServer <c>exportImage?f=json</c>, SOAP <c>ImageURL</c>) to an absolute URL.
/// </summary>
/// <remarks>
/// ArcGIS Server always returns an absolute output URL, and Esri-protocol clients request it
/// verbatim: ArcGIS Pro, arcpy and the QGIS ArcGIS Map Service provider all fail to draw the
/// layer when the envelope carries a root-relative path such as <c>/temp/{id}.png</c>.
/// The temporary file store yields that root-relative path, so every envelope goes through here.
/// </remarks>
internal static class GeoServicesImageHrefResolver
{
    internal static string ResolveAbsoluteHref(HttpContext context, string href)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(href);

        if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteHref)
            && (absoluteHref.Scheme == Uri.UriSchemeHttp || absoluteHref.Scheme == Uri.UriSchemeHttps))
        {
            return href;
        }

        return $"{BaseUrlResolver.GetBaseUrl(context)}{(href.StartsWith('/') ? string.Empty : "/")}{href}";
    }
}
