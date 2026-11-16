// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Honua.Scene.Grpc;

internal static class SceneCatalogPageToken
{
    private const string Prefix = "scene:v1:";

    public static string Encode(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var payload = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{Prefix}{offset}"));
        return Base64Url.EncodeToString(payload);
    }

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
