// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

// A response authenticated by a URI credential is marked private, and an existing no-store or private directive is never weakened.
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
