// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Shared helper that formats published artifacts as canonical
/// <c>data:&lt;content-type&gt;;base64,&lt;payload&gt;</c> URIs. Centralized so every
/// GDAL worker executor emits the same envelope shape and the upstream callers
/// (artifact projection, scalar reconciliation) parse them uniformly.
/// </summary>
internal static class GdalDataUri
{
    /// <summary>
    /// Builds a canonical data URI with the given MIME type and base64-encoded
    /// payload. Sized to the encoded length up front to avoid repeated buffer
    /// growth for large rasters.
    /// </summary>
    public static string Build(string contentType, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(payload);

        // base64 encodes 3 input bytes as 4 output chars (rounded up to a 4-char
        // group), plus the "data:" + ";base64," envelope and the content type.
        var base64Length = (payload.Length + 2) / 3 * 4;
        var sb = new StringBuilder(base64Length + contentType.Length + 16);
        sb.Append("data:");
        sb.Append(contentType);
        sb.Append(";base64,");
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }
}
