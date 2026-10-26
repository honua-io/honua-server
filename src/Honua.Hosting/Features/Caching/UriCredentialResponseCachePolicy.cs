// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Marks responses to requests that authenticated with a credential carried in the request
/// URI (the GeoServices <c>token</c> query parameter) as <c>Cache-Control: private</c>, as
/// RFC 6750 §2.3 requires, so no shared cache stores a response keyed by a URL that contains
/// the credential.
/// </summary>
/// <remarks>
/// The directive is merged, never downgraded: a response that is already <c>no-store</c> or
/// <c>private</c> is left alone, <c>public</c> is replaced by <c>private</c> (dropping
/// <c>s-maxage</c>, which only shared caches honour), and any other policy gains
/// <c>private</c>. <see cref="AuthenticationResponseCachePolicy"/> still runs afterwards and
/// adds <c>no-store</c> for credentialed identities; this policy keeps the RFC 6750
/// directive explicit and covers hosts or responses that policy does not reach.
/// </remarks>
internal static class UriCredentialResponseCachePolicy
{
    internal static void Apply(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var headers = response.GetTypedHeaders();
        var cacheControl = headers.CacheControl;
        if (cacheControl is null)
        {
            var raw = response.Headers.CacheControl.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                response.Headers.CacheControl = "private";
            }
            else if (!raw.Contains("no-store", StringComparison.OrdinalIgnoreCase))
            {
                // An unparseable policy is kept as written, but must still exclude shared caches.
                response.Headers.Append(HeaderNames.CacheControl, "private");
            }

            return;
        }

        if (cacheControl.NoStore || (cacheControl.Private && !cacheControl.Public))
        {
            return;
        }

        cacheControl.Public = false;
        cacheControl.Private = true;
        cacheControl.SharedMaxAge = null;
        headers.CacheControl = cacheControl;
    }
}
