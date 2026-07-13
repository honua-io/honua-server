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

        return values
            .Where(headerValue => headerValue is not null)
            .SelectMany(headerValue => headerValue!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Any(token => Version.TryParse(token, out var maxVersion) && maxVersion < CurrentParsedVersion);
    }

    private static void AppendVary(IHeaderDictionary headers, string value)
    {
        if (!headers.TryGetValue(VaryHeader, out var existing) || StringValues.IsNullOrEmpty(existing))
        {
            headers[VaryHeader] = value;
            return;
        }

        var alreadyPresent = existing.Any(headerValue =>
            headerValue != null &&
            headerValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)));

        if (!alreadyPresent)
        {
            headers[VaryHeader] = StringValues.Concat(existing, value);
        }
    }
}
