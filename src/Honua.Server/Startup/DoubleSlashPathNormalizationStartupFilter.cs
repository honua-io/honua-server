// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Startup;

internal sealed class DoubleSlashPathNormalizationStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                var path = context.Request.Path.Value;
                if (!string.IsNullOrEmpty(path) &&
                    path.Contains("//", StringComparison.Ordinal) &&
                    !IsMissingAdminConnectionIdPath(path))
                {
                    context.Request.Path = new PathString(CollapseConsecutiveSlashes(path));
                }

                return nextMiddleware(context);
            });

            next(app);
        };
    }

    internal static bool IsMissingAdminConnectionIdPath(string path)
    {
        var trimmedPath = path.TrimEnd('/');
        return trimmedPath.Contains("/admin/connections//", StringComparison.OrdinalIgnoreCase) &&
               trimmedPath.EndsWith("/tables", StringComparison.OrdinalIgnoreCase);
    }

    internal static string CollapseConsecutiveSlashes(string path)
    {
        var normalizedLength = CountCollapsedLength(path);
        if (normalizedLength == path.Length)
        {
            return path;
        }

        return string.Create(normalizedLength, path, CopyWithoutConsecutiveSlashes);
    }

    private static int CountCollapsedLength(string path)
    {
        var normalizedLength = 0;
        var previousWasSlash = false;
        for (var i = 0; i < path.Length; i++)
        {
            var isSlash = path[i] == '/';
            if (isSlash && previousWasSlash)
            {
                continue;
            }

            previousWasSlash = isSlash;
            normalizedLength++;
        }

        return normalizedLength;
    }

    private static void CopyWithoutConsecutiveSlashes(Span<char> destination, string source)
    {
        var writeIndex = 0;
        var lastWasSlash = false;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var isSlash = c == '/';
            if (isSlash && lastWasSlash)
            {
                continue;
            }

            lastWasSlash = isSlash;
            destination[writeIndex++] = c;
        }
    }
}
