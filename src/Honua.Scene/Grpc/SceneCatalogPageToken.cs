// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Honua.Scene.Grpc;

/// <summary>
/// Opaque continuation token for the gRPC <c>SceneService.ListScenes</c> catalog page.
/// </summary>
/// <remarks>
/// Geospatial.Grpc 0.2.0-alpha.1 retired the <c>result_offset</c> / <c>result_record_count</c>
/// integer pagination on <c>ListScenesRequest</c> in favour of <c>page_size</c> plus an opaque
/// <c>page_token</c>, and replaced <c>ListScenesResponse.exceeded_transfer_limit</c> with
/// <c>next_page_token</c>. The scene catalog is a small, ordered, in-memory merge (see
/// <c>SceneCatalog</c>), so an offset remains the natural cursor; this type keeps that offset
/// opaque on the wire so the encoding can change later without a proto change.
/// <para>
/// The token is a base64url-encoded <c>scene:v1:{offset}</c> string. It is deliberately not
/// signed or encrypted: it carries no tenant, identity, or authorization data, and the catalog it
/// indexes is the same ungated discovery listing every caller already sees.
/// </para>
/// </remarks>
internal static class SceneCatalogPageToken
{
    private const string Prefix = "scene:v1:";

    /// <summary>
    /// Encodes a zero-based catalog offset as an opaque continuation token.
    /// </summary>
    public static string Encode(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var payload = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{Prefix}{offset}"));
        return Base64Url.EncodeToString(payload);
    }

    /// <summary>
    /// Decodes an opaque continuation token back to its zero-based catalog offset.
    /// Returns <see langword="false"/> for a token this server did not issue.
    /// </summary>
    public static bool TryDecode(string? token, out int offset)
    {
        offset = 0;

        if (string.IsNullOrEmpty(token))
        {
            return true;
        }

        byte[] payload;
        try
        {
            payload = Base64Url.DecodeFromChars(token);
        }
        catch (FormatException)
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(payload);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!decoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(
            decoded.AsSpan(Prefix.Length),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out offset);
    }
}
