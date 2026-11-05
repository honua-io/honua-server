// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

// The admin session cookie is SameSite=Lax so the SAML ACS POST can set it, an unsafe request needs a same-site Sec-Fetch-Site or a same-host Origin, and a request with neither header is treated as non-browser.
internal static class AdminSessionCrossSiteRequestGuard
{
    private const string SecFetchSiteHeader = "Sec-Fetch-Site";

    // An unsafe method a browser marked cross-site must not be authenticated by the session cookie.
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
