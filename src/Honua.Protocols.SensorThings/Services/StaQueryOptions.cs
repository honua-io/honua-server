// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.SensorThings.Services;

/// <summary>
/// Parsed STA OData-style system query options. STA uses the same option names as
/// OData (<c>$filter</c>, <c>$orderby</c>, <c>$select</c>, <c>$expand</c>, <c>$top</c>,
/// <c>$skip</c>, <c>$count</c>); this binds them from the query string and clamps paging.
/// </summary>
internal sealed record StaQueryOptions
{
    /// <summary>Default page size when <c>$top</c> is not supplied (STA default is 100).</summary>
    public const int DefaultTop = 100;

    /// <summary>Maximum page size the server will honor for <c>$top</c>.</summary>
    public const int MaxTop = 1000;

    /// <summary>The raw <c>$filter</c> expression, if present.</summary>
    public string? Filter { get; init; }

    /// <summary>The raw <c>$orderby</c> clause, if present.</summary>
    public string? OrderBy { get; init; }

    /// <summary>The raw <c>$expand</c> clause, if present.</summary>
    public string? Expand { get; init; }

    /// <summary>The raw <c>$select</c> clause, if present.</summary>
    public string? Select { get; init; }

    /// <summary>Clamped page size (<c>$top</c>).</summary>
    public int Top { get; init; } = DefaultTop;

    /// <summary>Number of leading rows to skip (<c>$skip</c>).</summary>
    public int Skip { get; init; }

    /// <summary>Whether <c>$count=true</c> was requested.</summary>
    public bool Count { get; init; }

    /// <summary>True when <c>$orderby</c> requests descending order on the leading property.</summary>
    public bool OrderByDescending =>
        OrderBy is not null && OrderBy.TrimEnd().EndsWith(" desc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true when <c>$expand</c> names the given navigation property.</summary>
    public bool ExpandsTo(string navigation) =>
        Expand is not null && Expand
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part =>
            {
                // Strip nested option parentheses, e.g. "Observations($top=5)".
                var name = part;
                var paren = name.IndexOf('(', StringComparison.Ordinal);
                if (paren >= 0)
                {
                    name = name[..paren];
                }

                return string.Equals(name.Trim(), navigation, StringComparison.OrdinalIgnoreCase);
            });

    /// <summary>Binds query options from the request query string, clamping paging values.</summary>
    public static StaQueryOptions FromRequest(HttpRequest request)
    {
        var query = request.Query;

        var top = DefaultTop;
        if (TryGetInt(query, "$top", out var parsedTop) && parsedTop >= 0)
        {
            top = Math.Min(parsedTop, MaxTop);
        }

        var skip = 0;
        if (TryGetInt(query, "$skip", out var parsedSkip) && parsedSkip >= 0)
        {
            skip = parsedSkip;
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

    private static bool TryGetInt(IQueryCollection query, string key, out int value)
    {
        value = 0;
        return query.TryGetValue(key, out var raw) &&
            int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
