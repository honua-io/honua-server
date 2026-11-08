// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Services;

internal static class ConsoleContentSelection
{
    private const int MaxLimit = 200;

    public static ConsoleContentListResult Page(IEnumerable<ConsoleContentItem> source, ConsoleContentQuery query)
    {
        var limit = query.Limit <= 0 ? 25 : Math.Min(query.Limit, MaxLimit);
        var filtered = ApplyFilters(source, query).OrderBy(static i => i.Id, StringComparer.Ordinal).ToList();
        var startIndex = ResolveCursorIndex(query.Cursor, filtered);
        var page = filtered.Skip(startIndex).Take(limit).ToList();
        string? nextCursor = null;
        if (startIndex + page.Count < filtered.Count)
        {
            nextCursor = EncodeCursor(startIndex + page.Count);
        }
        IReadOnlyList<ConsoleContentItem> pageView = page;
        return new ConsoleContentListResult
        {
            Items = pageView,
            Total = filtered.Count,
            NextCursor = nextCursor,
        };
    }

    private static IEnumerable<ConsoleContentItem> ApplyFilters(IEnumerable<ConsoleContentItem> source, ConsoleContentQuery query)
    {
        if (query.ItemTypes is { Count: > 0 } types)
        {
            source = source.Where(i => types.Contains(i.ItemType));
        }
        if (query.Visibilities is { Count: > 0 } visibilities)
        {
            source = source.Where(i => visibilities.Contains(i.Visibility));
        }
        if (!string.IsNullOrWhiteSpace(query.OwnerId))
        {
            source = source.Where(i => string.Equals(i.OwnerId, query.OwnerId, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(query.Namespace))
        {
            source = source.Where(i => string.Equals(i.Namespace, query.Namespace, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var term = query.SearchTerm.Trim();
            source = source.Where(i => Matches(i, term));
        }
        return source;
    }

    private static bool Matches(ConsoleContentItem item, string term)
    {
        return item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
               || (item.Title is { } title && title.Contains(term, StringComparison.OrdinalIgnoreCase))
               || (item.Description is { } description && description.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static int ResolveCursorIndex(string? cursor, List<ConsoleContentItem> source)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? Math.Clamp(index, 0, source.Count)
                : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static string EncodeCursor(int index)
    {
        var bytes = Encoding.UTF8.GetBytes(index.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(bytes);
    }
}
