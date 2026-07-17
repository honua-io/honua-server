// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Opaque-cursor pagination helpers for the MCP operator surface (#1953). MCP
/// 2025-03-26 paginates list operations (<c>tools/list</c>, <c>resources/list</c>,
/// <c>resources/templates/list</c>, <c>prompts/list</c>) with an opaque
/// <c>cursor</c> request field and a <c>nextCursor</c> response field; this helper
/// produces and validates those cursors over a deterministically ordered list.
/// It also implements a Honua extension that chunks a large
/// <c>resources/read</c> payload into windowed pages so job-results and catalog
/// documents do not have to be returned in a single response.
/// </summary>
/// <remarks>
/// Cursors are opaque base64url tokens that callers MUST treat as black boxes and
/// echo verbatim — their internal shape (a stable offset into the ordered list,
/// or a content-index/character-offset pair for reads) is an implementation
/// detail and may change. An unparseable or out-of-range cursor surfaces as a
/// <see cref="GeoprocessingValidationException"/> so the dispatcher maps it to the
/// JSON-RPC <c>-32602</c> invalid-params error MCP requires for an invalid cursor.
/// </remarks>
internal static class McpPagination
{
    /// <summary>
    /// Default maximum number of list entries returned per page when the host
    /// does not override <see cref="McpSurfaceLimits.ListPageSize"/>.
    /// </summary>
    public const int DefaultListPageSize = 50;

    /// <summary>
    /// Default maximum number of characters returned per <c>resources/read</c>
    /// page. Sized large enough that ordinary resource documents are returned in
    /// a single response (no <c>nextCursor</c>) and only genuinely large
    /// job-results/catalog payloads are chunked.
    /// </summary>
    public const int DefaultMaxResourceReadChars = 1_000_000;

    private const string ListCursorPrefix = "l1:";
    private const string ReadCursorPrefix = "r1:";

    /// <summary>
    /// Returns the page of <paramref name="ordered"/> identified by
    /// <paramref name="cursor"/>, emitting <paramref name="nextCursor"/> when more
    /// entries remain. <paramref name="ordered"/> must already be in a stable
    /// order so the offset a cursor encodes is meaningful across calls.
    /// </summary>
    /// <exception cref="GeoprocessingValidationException">
    /// The cursor is malformed or points past the end of the list.
    /// </exception>
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

    /// <summary>
    /// Chunks the <paramref name="contents"/> of a <c>resources/read</c> result so
    /// no single page exceeds <paramref name="maxChars"/> characters, preserving
    /// each content block's <c>uri</c> and <c>mimeType</c>. When the whole
    /// document fits in one page and no cursor was supplied the original
    /// <paramref name="contents"/> are returned unchanged (no chunking, no
    /// <paramref name="nextCursor"/>) so small resources are byte-for-byte
    /// identical to the un-paginated behavior. Each emitted page's <c>text</c> is a
    /// fragment that a client concatenates per <c>uri</c> to reconstruct the full
    /// document.
    /// </summary>
    /// <exception cref="GeoprocessingValidationException">
    /// The cursor is malformed or points past the end of the contents.
    /// </exception>
    public static IReadOnlyList<McpResourceContent> Chunk(
        IReadOnlyList<McpResourceContent> contents,
        string? cursor,
        int maxChars,
        out string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var totalChars = 0L;
        foreach (var content in contents)
        {
            totalChars += content.Text.Length;
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
                || (contentIndex < contents.Count && (charOffset < 0 || charOffset > contents[contentIndex].Text.Length)))
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
            if (offset >= content.Text.Length)
            {
                i++;
                offset = 0;
                continue;
            }

            var take = Math.Min(budget, content.Text.Length - offset);
            page.Add(new McpResourceContent
            {
                Uri = content.Uri,
                MimeType = content.MimeType,
                Text = content.Text.Substring(offset, take),
            });
            offset += take;
            budget -= take;
            if (offset >= content.Text.Length)
            {
                i++;
                offset = 0;
            }
        }

        // Normalize the resume position past any fully consumed/empty blocks so a
        // trailing empty content does not produce a dangling nextCursor.
        while (i < contents.Count && offset >= contents[i].Text.Length)
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

/// <summary>
/// Host-tunable limits for the MCP operator surface: the list-pagination page
/// size and the per-page character budget for chunked <c>resources/read</c>
/// responses. Injected into <see cref="McpDataAccessSurface"/>; defaults to
/// <see cref="Default"/> when the host does not register an override.
/// </summary>
internal sealed record McpSurfaceLimits(int ListPageSize, int MaxResourceReadChars)
{
    /// <summary>
    /// The default limits: <see cref="McpPagination.DefaultListPageSize"/> list
    /// entries per page and <see cref="McpPagination.DefaultMaxResourceReadChars"/>
    /// characters per <c>resources/read</c> page.
    /// </summary>
    public static McpSurfaceLimits Default { get; } =
        new(McpPagination.DefaultListPageSize, McpPagination.DefaultMaxResourceReadChars);
}
