// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Honua.Infrastructure.Middleware;

// The request path an HTTP audit row records as its resource. A route parameter that carries a
// bearer credential (a share-link or download token) is replaced by its "{name}" placeholder, so
// the audit store and its exports never hold a usable link.
internal static class AuditResourcePath
{
    private static readonly string[] CredentialSuffixes = ["token", "secret", "signature", "password"];

    public static string? Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.Path.HasValue)
        {
            return null;
        }

        var path = context.Request.Path.Value!;
        if (context.GetEndpoint() is not RouteEndpoint endpoint)
        {
            return path;
        }

        foreach (var parameter in endpoint.RoutePattern.Parameters)
        {
            if (!IsCredentialParameter(parameter.Name))
            {
                continue;
            }

            // A value that is missing, or is not a whole path segment (catch-all, mixed segment,
            // other encoding), cannot be redacted in place, so record the template instead.
            if (!context.Request.RouteValues.TryGetValue(parameter.Name, out var value)
                || value?.ToString() is not { Length: > 0 } credential
                || RedactSegment(path, credential, "{" + parameter.Name + "}") is not { } redactedPath)
            {
                return endpoint.RoutePattern.RawText ?? string.Empty;
            }

            path = redactedPath;
        }

        return path;
    }

    internal static bool IsCredentialParameter(string name)
        => CredentialSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    // Returns the path with every segment equal to the credential replaced, or null when no
    // segment matched.
    private static string? RedactSegment(string path, string credential, string placeholder)
    {
        var segments = path.Split('/');
        var redacted = false;
        for (var index = 0; index < segments.Length; index++)
        {
            if (string.Equals(segments[index], credential, StringComparison.Ordinal)
                || string.Equals(Uri.UnescapeDataString(segments[index]), credential, StringComparison.Ordinal))
            {
                segments[index] = placeholder;
                redacted = true;
            }
        }

        return redacted ? string.Join('/', segments) : null;
    }
}
