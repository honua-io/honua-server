// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Infrastructure.Helpers;

internal static class ContentNegotiationHelpers
{
    public static bool TrySelectBestMediaType(
        IReadOnlyList<string> supportedMediaTypes,
        StringValues acceptHeader,
        out string selectedMediaType)
    {
        ArgumentNullException.ThrowIfNull(supportedMediaTypes);

        selectedMediaType = string.Empty;
        var acceptRanges = ParseAcceptHeader(acceptHeader);
        return !acceptRanges.IsDefaultOrEmpty &&
               TrySelectBestMediaType(supportedMediaTypes, acceptRanges, out selectedMediaType);
    }

    public static double GetBestQuality(
        IReadOnlyList<string> supportedMediaTypes,
        StringValues acceptHeader)
    {
        ArgumentNullException.ThrowIfNull(supportedMediaTypes);

        var acceptRanges = ParseAcceptHeader(acceptHeader);
        if (acceptRanges.IsDefaultOrEmpty)
        {
            return 0d;
        }

        var best = new AcceptSelection();
        var hasMatch = false;
        for (var i = 0; i < supportedMediaTypes.Count; i++)
        {
            if (!TryGetBestRangeMatch(supportedMediaTypes[i], acceptRanges, out var candidateSelection))
            {
                continue;
            }

            candidateSelection = candidateSelection with { SupportedOrder = i };
            if (!hasMatch || candidateSelection.IsPreferredTo(best))
            {
                best = candidateSelection;
                hasMatch = true;
            }
        }

        return hasMatch ? best.Quality : 0d;
    }

    public static ImmutableArray<AcceptMediaRange> ParseAcceptHeader(StringValues acceptHeader)
    {
        if (StringValues.IsNullOrEmpty(acceptHeader))
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<AcceptMediaRange>();
        var order = 0;

        foreach (var rawHeader in acceptHeader)
        {
            if (string.IsNullOrWhiteSpace(rawHeader))
            {
                continue;
            }

            foreach (var value in rawHeader.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = value.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                var segments = trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    continue;
                }

                if (!TryParseMediaType(segments[0], out var type, out var subtype))
                {
                    continue;
                }

                var quality = 1.0;
                for (var i = 1; i < segments.Length; i++)
                {
                    var parameter = segments[i].Trim();
                    if (!parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var qValue = parameter[2..].Trim();
                    if (double.TryParse(qValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        quality = Math.Clamp(parsed, 0.0, 1.0);
                    }

                    break;
                }

                if (quality <= 0.0)
                {
                    order++;
                    continue;
                }

                builder.Add(new AcceptMediaRange(type, subtype, quality, order));
                order++;
            }
        }

        return builder.ToImmutable();
    }

    private static bool TrySelectBestMediaType(
        IReadOnlyList<string> supportedMediaTypes,
        ImmutableArray<AcceptMediaRange> acceptRanges,
        out string selectedMediaType)
    {
        selectedMediaType = string.Empty;
        var best = new AcceptSelection();
        var hasMatch = false;

        for (var i = 0; i < supportedMediaTypes.Count; i++)
        {
            var candidate = supportedMediaTypes[i];
            if (!TryGetBestRangeMatch(candidate, acceptRanges, out var candidateSelection))
            {
                continue;
            }

            candidateSelection = candidateSelection with { SupportedOrder = i };
            if (!hasMatch || candidateSelection.IsPreferredTo(best))
            {
                best = candidateSelection;
                selectedMediaType = candidate;
                hasMatch = true;
            }
        }

        return hasMatch;
    }

    private static bool TryGetBestRangeMatch(
        string candidateMediaType,
        ImmutableArray<AcceptMediaRange> acceptRanges,
        out AcceptSelection bestSelection)
    {
        bestSelection = default;
        var hasMatch = false;

        for (var i = 0; i < acceptRanges.Length; i++)
        {
            var range = acceptRanges[i];
            if (!TryMatchMediaRange(range, candidateMediaType, out var specificity))
            {
                continue;
            }

            var selection = new AcceptSelection(range.Quality, specificity, range.Order, int.MaxValue);
            if (!hasMatch || selection.IsPreferredTo(bestSelection))
            {
                bestSelection = selection;
                hasMatch = true;
            }
        }

        return hasMatch;
    }

    private static bool TryMatchMediaRange(
        AcceptMediaRange range,
        string candidateMediaType,
        out int specificity)
    {
        specificity = -1;

        if (!TryParseMediaType(candidateMediaType, out var candidateType, out var candidateSubtype))
        {
            return false;
        }

        if (string.Equals(range.Type, "text", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(range.Subtype, "xml", StringComparison.OrdinalIgnoreCase) &&
            candidateSubtype.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
        {
            specificity = 0;
            return true;
        }

        var typeMatches = range.Type == "*" || string.Equals(range.Type, candidateType, StringComparison.OrdinalIgnoreCase);
        if (!typeMatches)
        {
            return false;
        }

        if (range.Subtype == "*")
        {
            specificity = range.Type == "*" ? 0 : 1;
            return true;
        }

        if (range.Subtype.StartsWith("*+", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = range.Subtype[1..];
            if (candidateSubtype.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                specificity = 1;
                return true;
            }

            return false;
        }

        if (string.Equals(range.Subtype, candidateSubtype, StringComparison.OrdinalIgnoreCase))
        {
            specificity = 2;
            return true;
        }

        if (string.Equals(range.Subtype, "xml", StringComparison.OrdinalIgnoreCase) &&
            candidateSubtype.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
        {
            specificity = 1;
            return true;
        }

        return false;
    }

    private static bool TryParseMediaType(string mediaType, out string type, out string subtype)
    {
        type = string.Empty;
        subtype = string.Empty;

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        var withoutParameters = mediaType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        var separator = withoutParameters.IndexOf('/');
        if (separator <= 0 || separator >= withoutParameters.Length - 1)
        {
            return false;
        }

        type = withoutParameters[..separator].Trim();
        subtype = withoutParameters[(separator + 1)..].Trim();

        return !(string.IsNullOrEmpty(type) || string.IsNullOrEmpty(subtype));
    }

    internal readonly record struct AcceptMediaRange(string Type, string Subtype, double Quality, int Order);

    private readonly record struct AcceptSelection(double Quality, int Specificity, int AcceptOrder, int SupportedOrder)
    {
        public bool IsPreferredTo(AcceptSelection other)
        {
            if (Quality != other.Quality)
            {
                return Quality > other.Quality;
            }

            if (Specificity != other.Specificity)
            {
                return Specificity > other.Specificity;
            }

            if (AcceptOrder != other.AcceptOrder)
            {
                return AcceptOrder < other.AcceptOrder;
            }

            return SupportedOrder < other.SupportedOrder;
        }
    }
}
