// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalDataUri
{
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
