// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Security;

internal static class HttpResponseClearHelper
{
    internal static void ClearPreservingCorsHeaders(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        List<(string Key, StringValues Value)>? preserved = null;
        foreach (var header in response.Headers)
        {
            if (header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, HeaderNames.Vary, StringComparison.OrdinalIgnoreCase))
            {
                preserved ??= new List<(string Key, StringValues Value)>(4);
                preserved.Add((header.Key, header.Value));
            }
        }

        response.Clear();

        if (preserved is not null)
        {
            foreach (var (key, value) in preserved)
            {
                response.Headers[key] = value;
            }
        }
    }
}
