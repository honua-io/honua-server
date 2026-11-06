// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Features.Publishing.Content.Services;

public static class ContentPublicationSlug
{
    public const int MaxLength = 200;

    public static string Normalize(string? requested, string? fallback = null)
    {
        if (TryNormalize(requested, out var slug))
        {
            return slug;
        }

        if (TryNormalize(fallback, out slug))
        {
            return slug;
        }

        return "pub-" + Guid.NewGuid().ToString("N")[..12];
    }

    public static bool TryNormalize(string? input, out string slug)
    {
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var lowered = input.Trim().ToLowerInvariant();

        // Reject path traversal sequences before any character folding.
        if (lowered.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var folded = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '/')
            {
                folded.Append(ch);
            }
            else
            {
                // Spaces, underscores, and any other punctuation fold to a dash.
                folded.Append('-');
            }
        }

        var rawSegments = folded.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = rawSegments
            .Select(raw => CollapseDashes(raw).Trim('-'))
            .Where(segment => segment.Length > 0)
            .ToList();

        if (segments.Count == 0)
        {
            return false;
        }

        var result = string.Join('/', segments);
        if (result.Length == 0 || result.Length > MaxLength)
        {
            return false;
        }

        slug = result;
        return true;
    }

    private static string CollapseDashes(string value)
    {
        var sb = new StringBuilder(value.Length);
        var previousDash = false;
        foreach (var ch in value)
        {
            if (ch == '-')
            {
                if (!previousDash)
                {
                    sb.Append('-');
                }

                previousDash = true;
            }
            else
            {
                sb.Append(ch);
                previousDash = false;
            }
        }

        return sb.ToString();
    }
}
