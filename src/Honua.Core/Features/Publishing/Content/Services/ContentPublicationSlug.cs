// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Features.Publishing.Content.Services;

/// <summary>
/// Normalizes and validates content-publication route slugs. Slugs are
/// lowercase, hierarchical (path segments separated by <c>/</c>), and composed of
/// <c>[a-z0-9-]</c> per segment with each segment bounded by alphanumerics. The
/// normalization is idempotent on already-canonical slugs so the same routine can
/// canonicalize both the publish input and a lookup key.
/// </summary>
public static class ContentPublicationSlug
{
    /// <summary>Maximum slug length.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Normalizes the requested slug, falling back to the title, then to a generated
    /// id. Never throws.
    /// </summary>
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

    /// <summary>
    /// Attempts to canonicalize <paramref name="input"/> into a valid slug.
    /// </summary>
    /// <returns><see langword="true"/> when a non-empty canonical slug was produced.</returns>
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
        var segments = new List<string>(rawSegments.Length);
        foreach (var raw in rawSegments)
        {
            var segment = CollapseDashes(raw).Trim('-');
            if (segment.Length > 0)
            {
                segments.Add(segment);
            }
        }

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
