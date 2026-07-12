// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Content pre-check for the untrusted base64 blobs the geoprocessing submit path
/// projects onto raster/vector step inputs (#2765). GDAL opens a materialized
/// input by <em>sniffing its content</em>, not the claimed media type, so a caller
/// can base64-encode a GDAL <c>VRT</c> (or any XML service-description /
/// indirection format) whose <c>&lt;SourceFilename&gt;</c> points at a local path
/// (<c>/etc/passwd</c>, a mounted secret) or a <c>/vsicurl/</c> / <c>/vsis3/</c>
/// URL — a file-read / SSRF vector. This guard refuses such blobs BEFORE they are
/// ever written to scratch and handed to a GDAL CLI:
/// <list type="number">
/// <item>Any blob whose leading bytes look like XML (a VRT / OGR VRT / WMS / WMTS /
/// WCS / STAC service description are all XML): none of the raster or vector
/// formats the worker legitimately ingests over this path (GTiff, PNG, JPEG,
/// GeoJSON, GPKG, CSV, FlatGeobuf, ESRI Shapefile) begin with <c>&lt;</c>, so an
/// XML lead is always an indirection driver we exclude.</item>
/// <item>Any blob whose bytes contain a GDAL virtual-filesystem reference
/// (<c>/vsicurl</c>, <c>/vsis3</c>, <c>/vsihttp</c>, …): the remote/indirection VSI
/// handlers are the SSRF / file-read reach, and an untrusted ingest never has a
/// legitimate reason to embed one.</item>
/// </list>
/// This is the belt-and-suspenders pre-check; the driver-skip + remote-VSI-disable
/// subprocess environment (<see cref="GdalRuntimeHardening"/>) is the second,
/// independent control that blocks the same blob at the GDAL layer even if it were
/// to slip past this sniff.
/// </summary>
internal static class GdalUntrustedInputGuard
{
    /// <summary>
    /// The GDAL virtual-filesystem prefixes that reach off the local scratch
    /// workspace (remote object stores, arbitrary URLs, and archive indirection).
    /// Each is at least six bytes with a leading <c>/vsi</c>, so scanning a decoded
    /// payload for them as ASCII substrings has a negligible false-positive rate on
    /// legitimate binary rasters while catching an embedded reference in any text or
    /// XML input. Matched case-insensitively.
    /// </summary>
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

    /// <summary>
    /// Validates a decoded untrusted input, returning <see langword="false"/> with a
    /// caller-safe <paramref name="reason"/> when the blob is an XML/indirection
    /// format or embeds a virtual-filesystem reference.
    /// </summary>
    /// <param name="decoded">The base64-decoded input bytes.</param>
    /// <param name="reason">On rejection, a short reason suitable for a job-failure message.</param>
    /// <returns><see langword="true"/> when the blob is admissible; otherwise <see langword="false"/>.</returns>
    public static bool IsAdmissible(ReadOnlySpan<byte> decoded, out string reason)
    {
        reason = "";

        if (LooksLikeXml(decoded))
        {
            reason =
                "input content is XML — GDAL VRT / OGR VRT / WMS / WMTS / WCS / STAC "
                + "indirection formats are refused on the untrusted-input path (they can "
                + "reference arbitrary local files or remote URLs)";
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

    /// <summary>
    /// Reports whether the leading non-whitespace bytes indicate an XML document
    /// (an <c>&lt;?xml</c> declaration or an element open <c>&lt;</c>). A UTF-8 /
    /// UTF-16 BOM and leading ASCII whitespace are skipped first so a VRT written
    /// with a BOM or indented root still trips the check.
    /// </summary>
    private static bool LooksLikeXml(ReadOnlySpan<byte> decoded)
    {
        var span = decoded;

        // Skip a UTF-8 BOM.
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }
        // Skip a UTF-16 LE/BE BOM.
        else if (span.Length >= 2 && ((span[0] == 0xFF && span[1] == 0xFE) || (span[0] == 0xFE && span[1] == 0xFF)))
        {
            span = span[2..];
        }

        var i = 0;
        while (i < span.Length && IsAsciiWhitespace(span[i]))
        {
            i++;
        }

        // A leading '<' (after optional whitespace/BOM) is the XML element/declaration
        // open. UTF-16-encoded XML surfaces as '<' 0x00 or 0x00 '<'; both leave a '<'
        // as the first or second meaningful byte, which this catches.
        if (i < span.Length && span[i] == (byte)'<')
        {
            return true;
        }

        // UTF-16 BE without BOM: 0x00 '<'.
        return i + 1 < span.Length && span[i] == 0x00 && span[i + 1] == (byte)'<';
    }

    private static bool ContainsVsiReference(ReadOnlySpan<byte> decoded, out string matched)
    {
        foreach (var prefix in DangerousVsiPrefixes)
        {
            if (IndexOfAsciiIgnoreCase(decoded, prefix) >= 0)
            {
                matched = prefix;
                return true;
            }
        }

        matched = "";
        return false;
    }

    private static bool IsAsciiWhitespace(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0B or 0x0C;

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
