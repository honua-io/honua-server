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

    /// <summary>Fetch one extra row to detect continuation without emitting an empty final page.</summary>
    public int FetchTop => Top == 0 ? 0 : Top + 1;

    /// <summary>Number of leading rows to skip (<c>$skip</c>).</summary>
    public int Skip { get; init; }

    /// <summary>
    /// The <c>$skip</c> value that addresses the page after this one, or
    /// <see langword="null"/> when that offset would exceed <see cref="int.MaxValue"/>.
    /// The store pages on a 32-bit offset, so a wider continuation is one the server
    /// could not consume: callers must not emit a link for it.
    /// </summary>
    public int? NextSkip
    {
        get
        {
            var next = (long)Skip + Top;
            return next > int.MaxValue ? null : (int)next;
        }
    }

    /// <summary>Whether <c>$count=true</c> was requested.</summary>
    public bool Count { get; init; }

    // $orderby, $select and $expand are interpreted by StaQueryPlan against the target
    // entity's schema: the option's meaning depends on which properties the entity set has,
    // and an option this server cannot honour must fail the request rather than be dropped
    // here (#4201).

    /// <summary>Binds query options from the request query string, clamping paging values.</summary>
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

    /// <summary>
    /// Reads a non-negative paging option as a 64-bit value. A well-formed digit string
    /// too wide for <see cref="long"/> saturates to <see cref="long.MaxValue"/> so the
    /// caller clamps it, instead of the parse failing and the option defaulting.
    /// </summary>
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
