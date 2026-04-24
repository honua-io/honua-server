// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.OData.Services;

internal static class ODataProtocolHeaders
{
    internal const string CurrentVersion = "4.01";
    internal const string V4CompatibleVersion = "4.0";

    private const string ODataMaxVersionHeader = "OData-MaxVersion";
    private const string ODataVersionHeader = "OData-Version";
    private const string VaryHeader = "Vary";

    public static void SetVersionHeader(HttpContext context)
    {
        context.Response.Headers[ODataVersionHeader] = ResolveResponseVersion(context.Request);

        if (context.Request.Headers.ContainsKey(ODataMaxVersionHeader))
        {
            AppendVary(context.Response.Headers, ODataMaxVersionHeader);
        }
    }

    private static string ResolveResponseVersion(HttpRequest request)
    {
        return request.Headers.TryGetValue(ODataMaxVersionHeader, out var maxVersion) &&
            IsV4Only(maxVersion)
            ? V4CompatibleVersion
            : CurrentVersion;
    }

    private static bool IsV4Only(StringValues maxVersion)
    {
        foreach (var value in maxVersion)
        {
            if (string.Equals(value?.Trim(), V4CompatibleVersion, StringComparison.Ordinal))
            {
                return true;
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
