// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// CSRF guard for the cookie-authenticated admin session (#6100). The SAML ACS must issue
/// <c>honua_admin_session</c> as <c>SameSite=Lax</c> because the IdP POSTs to it cross-site, so the
/// cookie alone does not prove the request came from Honua's own pages. An unsafe request
/// (anything but GET/HEAD/OPTIONS/TRACE) authenticated by that cookie must therefore carry a
/// browser signal that it is not cross-site:
/// <list type="bullet">
/// <item><c>Sec-Fetch-Site</c>, when present, must be <c>same-origin</c>, <c>same-site</c> or <c>none</c>;</item>
/// <item>otherwise an <c>Origin</c> header, when present, must name the request host.</item>
/// </list>
/// A request with neither header is not from a browser that could be driven cross-site (every
/// current browser sends <c>Sec-Fetch-Site</c> on secure requests and <c>Origin</c> on cross-origin
/// POSTs), so non-browser cookie clients keep working.
/// </summary>
internal static class AdminSessionCrossSiteRequestGuard
{
    private const string SecFetchSiteHeader = "Sec-Fetch-Site";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="request"/> is an unsafe method that a
    /// browser marked as cross-site, so a session cookie on it must not authenticate it.
    /// </summary>
    public static bool IsCrossSiteUnsafeRequest(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var method = request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ||
            HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
        {
            return false;
        }

        var fetchSite = request.Headers[SecFetchSiteHeader].ToString();
        if (!string.IsNullOrEmpty(fetchSite))
        {
            return !(string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fetchSite, "same-site", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase));
        }

        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }

        // "null" (opaque origin: sandboxed frame, data: URL) and anything unparsable is cross-site.
        // Only the host is compared: a TLS-terminating proxy can change the scheme and port the
        // server sees, and a different port on the same host is same-site anyway.
        return !Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || !string.Equals(originUri.IdnHost, request.Host.Host, StringComparison.OrdinalIgnoreCase);
    }
}
