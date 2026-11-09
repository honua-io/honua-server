// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.SensorThings.Services;

internal sealed record StaQueryOptions
{
    public const int DefaultTop = 100;

    public const int MaxTop = 1000;

    // Nested expand pages are capped below the root page size so one expand cannot fan out at the full page size.
    public const int NestedMaxTop = 100;

    public string? Filter { get; init; }

    public string? OrderBy { get; init; }

    public string? Expand { get; init; }

    public string? Select { get; init; }

    public int Top { get; init; } = DefaultTop;

    public int FetchTop => Top == 0 ? 0 : Top + 1;

    public int Skip { get; init; }

    public int? NextSkip
    {
        get
        {
            var next = (long)Skip + Top;
            return next > int.MaxValue ? null : (int)next;
        }
    }

    public bool Count { get; init; }

    // $orderby, $select and $expand are interpreted by StaQueryPlan against the target
    // entity's schema: the option's meaning depends on which properties the entity set has,
    // and an option this server cannot honour must fail the request rather than be dropped
    // here (#4201).

    public static StaQueryOptions FromRequest(HttpRequest request)
    {
        var query = request.Query;

        var top = DefaultTop;
        if (TryGetPagingValue(query, "$top", out var parsedTop) && parsedTop >= 0)
        {
            top = (int)Math.Min(parsedTop, MaxTop);
        }

        var skip = 0;
        if (TryGetPagingValue(query, "$skip", out var parsedSkip) && parsedSkip >= 0)
        {
            // Clamp rather than reject: an offset past int.MaxValue is past every row a
            // 32-bit store offset can address, and falling back to 0 would silently
            // restart pagination at the first page instead of ending it.
            skip = (int)Math.Min(parsedSkip, int.MaxValue);
        }

        var count = false;
        if (query.TryGetValue("$count", out var countValue) &&
            bool.TryParse(countValue.ToString(), out var parsedCount))
        {
            count = parsedCount;
        }

        return new StaQueryOptions
        {
            Filter = GetString(query, "$filter"),
            OrderBy = GetString(query, "$orderby"),
            Expand = GetString(query, "$expand"),
            Select = GetString(query, "$select"),
            Top = top,
            Skip = skip,
            Count = count
        };
    }

    private static string? GetString(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : null;

    private static bool TryGetPagingValue(IQueryCollection query, string key, out long value)
    {
        value = 0;
        if (!query.TryGetValue(key, out var raw))
        {
            return false;
        }

        var text = raw.ToString().Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            value = long.MaxValue;
            return true;
        }

        return false;
    }
}
