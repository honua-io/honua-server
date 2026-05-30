// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.OData.Services;

internal static class ODataProtocolHeaders
{
    internal const string CurrentVersion = "4.01";
    internal const string V4CompatibleVersion = "4.0";
    private static readonly Version CurrentParsedVersion = new(4, 1);
    private const string ODataMaxVersionHeader = "OData-MaxVersion";
    private const string ODataVersionHeader = "OData-Version";
    private const string VaryHeader = "Vary";

    public static void SetVersionHeader(HttpContext context)
    {
        context.Response.Headers[ODataVersionHeader] = GetResponseVersion(context);

        if (context.Request.Headers.ContainsKey(ODataMaxVersionHeader))
        {
            AppendVary(context.Response.Headers, ODataMaxVersionHeader);
        }
    }

    public static string GetResponseVersion(HttpContext context)
        => RequestCapsResponseAtODataV4(context.Request.Headers)
            ? V4CompatibleVersion
            : CurrentVersion;

    private static bool RequestCapsResponseAtODataV4(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue(ODataMaxVersionHeader, out var values))
        {
            return false;
        }

        foreach (var headerValue in values)
        {
            if (headerValue is null)
            {
                continue;
            }

            foreach (var token in headerValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (Version.TryParse(token, out var maxVersion) && maxVersion < CurrentParsedVersion)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AppendVary(IHeaderDictionary headers, string value)
    {
        if (!headers.TryGetValue(VaryHeader, out var existing) || StringValues.IsNullOrEmpty(existing))
        {
            headers[VaryHeader] = value;
            return;
        }

        foreach (var headerValue in existing)
        {
            if (headerValue != null &&
                headerValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        headers[VaryHeader] = StringValues.Concat(existing, value);
    }
}
