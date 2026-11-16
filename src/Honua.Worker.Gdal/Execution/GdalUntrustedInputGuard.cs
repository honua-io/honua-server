// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

internal static class GdalUntrustedInputGuard
{
    private static readonly string[] IndirectionXmlMarkers =
    [
        "<VRTDataset",         // raster VRT
        "<OGRVRTDataSource",   // vector VRT
        "<GDAL_WMS",           // GDAL WMS service description
        "<GDAL_WMTS",          // GDAL WMTS service description
        "<WMS_GDAL",           // legacy WMS service description alias
        "<WMTS_GDAL",          // legacy WMTS service description alias
        "<WCS_GDAL",           // GDAL WCS service description
        "<WMT_MS_Capabilities", // WMS 1.1 capabilities (openable by the WMS driver)
        "<WMS_Capabilities",   // WMS 1.3 capabilities (openable by the WMS driver)
    ];

    private static readonly string[] DangerousVsiPrefixes =
    [
        "/vsicurl",
        "/vsis3",
        "/vsigs",
        "/vsiaz",
        "/vsioss",
        "/vsiswift",
        "/vsihdfs",
        "/vsiwebhdfs",
        "/vsihttp",
        "/vsizip",
        "/vsitar",
        "/vsigzip",
        "/vsi7z",
        "/vsirar",
        "/vsimem",
        "/vsistdin",
    ];

    public static bool IsAdmissible(ReadOnlySpan<byte> decoded, out string reason)
    {
        reason = "";

        if (ContainsIndirectionXmlMarker(decoded, out var marker))
        {
            reason =
                $"input content is a GDAL indirection / service XML format ('{marker}') — "
                + "VRT / OGR VRT / WMS / WMTS / WCS blobs are refused on the untrusted-input "
                + "path because they can reference arbitrary local files or remote URLs "
                + "(KML / GML and other data formats remain accepted)";
            return false;
        }

        if (ContainsVsiReference(decoded, out var matched))
        {
            reason =
                $"input content references the GDAL virtual filesystem ('{matched}') — "
                + "remote / indirection VSI handlers are refused on the untrusted-input path";
            return false;
        }

        return true;
    }

    private static bool ContainsIndirectionXmlMarker(ReadOnlySpan<byte> decoded, out string marker)
    {
        // Not a .Where(...)/.FirstOrDefault(...) candidate: decoded is a ReadOnlySpan<byte>
        // (a ref struct), which cannot be captured in a LINQ lambda closure.
        foreach (var candidate in IndirectionXmlMarkers)
        {
            switch (IndexOfAsciiIgnoreCase(decoded, candidate))
            {
                case >= 0:
                    marker = candidate;
                    return true;
            }
        }

        marker = "";
        return false;
    }

    private static bool ContainsVsiReference(ReadOnlySpan<byte> decoded, out string matched)
    {
        // Not a .Where(...)/.FirstOrDefault(...) candidate: decoded is a ReadOnlySpan<byte>
        // (a ref struct), which cannot be captured in a LINQ lambda closure.
        foreach (var prefix in DangerousVsiPrefixes)
        {
            switch (IndexOfAsciiIgnoreCase(decoded, prefix))
            {
                case >= 0:
                    matched = prefix;
                    return true;
            }
        }

        matched = "";
        return false;
    }

    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> haystack, string needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        var last = haystack.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (ToLowerAscii(haystack[i + j]) != ToLowerAscii((byte)needle[j]))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static byte ToLowerAscii(byte b)
        => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
}
