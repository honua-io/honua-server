// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Infrastructure.Validation;

internal static class XmlContentNegotiation
{
    public static bool IsXmlAccepted(string? acceptHeader)
    {
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return true;
        }

        var hasExplicitType = false;
        var hasSupportedExplicitType = false;
        var hasWildcardType = false;

        var mediaTypes = acceptHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var mediaTypeWithParameters in mediaTypes)
        {
            if (IsRejectedByQuality(mediaTypeWithParameters))
            {
                continue;
            }

            var mediaType = mediaTypeWithParameters.Split(';', 2, StringSplitOptions.TrimEntries)[0];
            if (mediaType.Length == 0)
            {
                continue;
            }

            if (string.Equals(mediaType, "*/*", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mediaType, "application/*", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mediaType, "text/*", StringComparison.OrdinalIgnoreCase))
            {
                hasWildcardType = true;
                continue;
            }

            hasExplicitType = true;
            if (string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mediaType, "text/xml", StringComparison.OrdinalIgnoreCase) ||
                mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
            {
                hasSupportedExplicitType = true;
            }
        }

        if (hasSupportedExplicitType)
        {
            return true;
        }

        if (hasExplicitType)
        {
            return false;
        }

        return hasWildcardType;
    }

    private static bool IsRejectedByQuality(string mediaTypeWithParameters)
    {
        foreach (var parameter in mediaTypeWithParameters.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = parameter.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                string.Equals(parts[0], "q", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var quality) &&
                quality <= 0)
            {
                return true;
            }
        }

        return false;
    }
}
