// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

// Adds request headers to Vary for the shared-cache, validator and scene paths. Compares whole tokens, so an existing
// Accept-Encoding entry does not hide a missing Accept, and appends, so entries written earlier in the pipeline survive.
internal static class VaryHeader
{
    // Each value may itself be a comma-separated list.
    internal static void AppendMissing(IHeaderDictionary headers, params ReadOnlySpan<string> values)
    {
        var existing = headers.Vary;
        if (ContainsToken(existing, "*"))
        {
            return;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var span = value.AsSpan();
            foreach (var range in span.Split(','))
            {
                var requestHeader = span[range].Trim();
                if (requestHeader.IsEmpty)
                {
                    continue;
                }

                if (!ContainsToken(existing, requestHeader))
                {
                    var headerText = requestHeader.Length == value.Length ? value : requestHeader.ToString();
                    headers.AppendCommaSeparatedValues(HeaderNames.Vary, headerText);
                    existing = headers.Vary;
                    if (requestHeader.SequenceEqual("*"))
                    {
                        return;
                    }
                }
            }
        }
    }

    private static bool ContainsToken(StringValues values, ReadOnlySpan<char> token)
    {
        foreach (var entry in values)
        {
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            var span = entry.AsSpan();
            foreach (var range in span.Split(','))
            {
                var item = span[range].Trim();
                if (!item.IsEmpty && item.Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
