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

        var sb = new StringBuilder(payload.Length * 2 + contentType.Length + 32);
        sb.Append("data:");
        sb.Append(contentType);
        sb.Append(";base64,");
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }
}
