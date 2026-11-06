// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp;

internal static class McpPagination
{
    public const int DefaultListPageSize = 12;

    // A read without a cursor returns the whole resource because MCP resources/read defines no cursor.
    public const int DefaultMaxResourceReadChars = 64_000;

    public const int MaxResourceReadChars = 1_000_000;

    private const string ListCursorPrefix = "l1:";
    private const string ReadCursorPrefix = "r1:";

    public static IReadOnlyList<T> Page<T>(
        IReadOnlyList<T> ordered,
        string? cursor,
        int pageSize,
        out string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        if (pageSize <= 0)
        {
            nextCursor = null;
            return ordered;
        }

        var offset = 0;
        if (!string.IsNullOrEmpty(cursor))
        {
            offset = DecodeListCursor(cursor);
            if (offset < 0 || offset > ordered.Count)
            {
                throw new GeoprocessingValidationException("The pagination cursor is invalid or has expired.");
            }
        }

        var end = Math.Min(offset + pageSize, ordered.Count);
        var page = new List<T>(end - offset);
        for (var i = offset; i < end; i++)
        {
            page.Add(ordered[i]);
        }

        nextCursor = end < ordered.Count ? EncodeListCursor(end) : null;
        return page;
    }

    public static IReadOnlyList<McpResourceContent> Chunk(
        IReadOnlyList<McpResourceContent> contents,
        string? cursor,
        int maxChars,
        out string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(contents);

        if (contents.Any(content => content.Blob is not null))
        {
            if (!string.IsNullOrEmpty(cursor))
            {
                throw new GeoprocessingValidationException("Binary resources do not support a pagination cursor.");
            }
            var binaryChars = contents.Sum(content => (long)(content.Blob?.Length ?? 0) + (content.Text?.Length ?? 0));
            if (binaryChars > maxChars || binaryChars > MaxResourceReadChars)
            {
                throw new GeoprocessingValidationException(
                    $"Binary resources cannot be chunked; retry with maxChars of at least {binaryChars}, up to {MaxResourceReadChars}, or render a smaller image.");
            }
            nextCursor = null;
            return contents;
        }

        var totalChars = 0L;
        foreach (var content in contents)
        {
            totalChars += (content.Text?.Length ?? 0);
        }

        // Fast path: an unpaginated read of a document that fits in one page is
        // returned verbatim so existing resources keep identical wire output.
        if (string.IsNullOrEmpty(cursor) && (maxChars <= 0 || totalChars <= maxChars))
        {
            nextCursor = null;
            return contents;
        }

        if (maxChars <= 0)
        {
            nextCursor = null;
            return contents;
        }

        var contentIndex = 0;
        var charOffset = 0;
        if (!string.IsNullOrEmpty(cursor))
        {
            (contentIndex, charOffset) = DecodeReadCursor(cursor);
            if (contentIndex < 0
                || contentIndex > contents.Count
                || (contentIndex < contents.Count && (charOffset < 0 || charOffset > (contents[contentIndex].Text?.Length ?? 0))))
            {
                throw new GeoprocessingValidationException("The pagination cursor is invalid or has expired.");
            }
        }

        var page = new List<McpResourceContent>();
        var budget = maxChars;
        var i = contentIndex;
        var offset = charOffset;
        while (i < contents.Count && budget > 0)
        {
            var content = contents[i];
            if (offset >= (content.Text?.Length ?? 0))
            {
                i++;
                offset = 0;
                continue;
            }

            var take = Math.Min(budget, (content.Text?.Length ?? 0) - offset);
            page.Add(new McpResourceContent
            {
                Uri = content.Uri,
                MimeType = content.MimeType,
                Text = content.Text!.Substring(offset, take),
            });
            offset += take;
            budget -= take;
            if (offset >= (content.Text?.Length ?? 0))
            {
                i++;
                offset = 0;
            }
        }

        // Normalize the resume position past any fully consumed/empty blocks so a
        // trailing empty content does not produce a dangling nextCursor.
        while (i < contents.Count && offset >= (contents[i].Text?.Length ?? 0))
        {
            i++;
            offset = 0;
        }

        nextCursor = i < contents.Count ? EncodeReadCursor(i, offset) : null;
        return page;
    }

    private static string EncodeListCursor(int offset) =>
        Encode(ListCursorPrefix + offset.ToString(CultureInfo.InvariantCulture));

    private static int DecodeListCursor(string cursor)
    {
        var token = Decode(cursor);
        if (token is null || !token.StartsWith(ListCursorPrefix, StringComparison.Ordinal))
        {
            throw new GeoprocessingValidationException("The pagination cursor is invalid or has expired.");
        }

        if (!int.TryParse(
                token.AsSpan(ListCursorPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offset))
        {
            throw new GeoprocessingValidationException("The pagination cursor is invalid or has expired.");
        }

        return offset;
    }

    private static string EncodeReadCursor(int contentIndex, int charOffset) =>
        Encode(ReadCursorPrefix
            + contentIndex.ToString(CultureInfo.InvariantCulture)
            + ':'
            + charOffset.ToString(CultureInfo.InvariantCulture));

    private static (int ContentIndex, int CharOffset) DecodeReadCursor(string cursor)
    {
        var token = Decode(cursor);
        if (token is null || !token.StartsWith(ReadCursorPrefix, StringComparison.Ordinal))
        {
            throw new GeoprocessingValidationException("The pagination cursor is invalid or has expired.");
        }

        var body = token.AsSpan(ReadCursorPrefix.Length);
        var separator = body.IndexOf(':');
        if (separator <= 0
            || !int.TryParse(body[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var contentIndex)
            || !int.TryParse(body[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var charOffset))
        {
            throw new GeoprocessingValidationException("The pagination cursor is invalid or has expired.");
        }

        return (contentIndex, charOffset);
    }

    private static string Encode(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        return Base64Url.EncodeToString(bytes);
    }

    private static string? Decode(string cursor)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

internal sealed record McpSurfaceLimits(int ListPageSize, int MaxResourceReadChars)
{
    public static McpSurfaceLimits Default { get; } =
        new(McpPagination.DefaultListPageSize, McpPagination.DefaultMaxResourceReadChars);
}
