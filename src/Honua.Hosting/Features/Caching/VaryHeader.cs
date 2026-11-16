// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

// Adds request headers to Vary for the shared-cache, validator and scene paths. Compares whole tokens, so an existing
// Accept-Encoding entry does not hide a missing Accept, and appends, so entries written earlier in the pipeline survive.
internal static class VaryHeader
{
    // Each value may itself be a comma-separated list.
    internal static void AppendMissing(IHeaderDictionary headers, params ReadOnlySpan<string> values)
    {
        foreach (var value in values)
        {
            foreach (var requestHeader in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var present = headers.GetCommaSeparatedValues(HeaderNames.Vary);
                if (!present.Contains("*", StringComparer.Ordinal) &&
                    !present.Contains(requestHeader, StringComparer.OrdinalIgnoreCase))
                {
                    headers.AppendCommaSeparatedValues(HeaderNames.Vary, requestHeader);
                }
            }
        }
    }
}
