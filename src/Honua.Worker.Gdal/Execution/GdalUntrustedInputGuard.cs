// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Content pre-check for the untrusted base64 blobs the geoprocessing submit path
/// projects onto raster/vector step inputs (#2765). GDAL opens a materialized
/// input by <em>sniffing its content</em>, not the claimed media type, so a caller
/// can base64-encode a GDAL <c>VRT</c> (or a WMS/WMTS/WCS service description) whose
/// <c>&lt;SourceFilename&gt;</c> / service URL points at a local path
/// (<c>/etc/passwd</c>, a mounted secret) or a <c>/vsicurl/</c> / <c>/vsis3/</c>
/// URL — a file-read / SSRF vector. This guard refuses such blobs BEFORE they are
/// ever written to scratch and handed to a GDAL CLI:
/// <list type="number">
/// <item>Any blob whose bytes contain a GDAL <em>indirection / service</em> XML root
/// (<c>&lt;VRTDataset</c>, <c>&lt;OGRVRTDataSource</c>, a GDAL WMS/WMTS/WCS service
/// description, or a WMS capabilities document). These are the XML formats that
/// dereference an external file or URL on open. Crucially this is a targeted set,
/// NOT "any XML": the <c>source.ogr</c> reader legitimately ingests KML and GML —
/// which are also XML — so a blanket XML refusal would break those imports.</item>
/// <item>Any blob whose bytes contain a GDAL virtual-filesystem reference
/// (<c>/vsicurl</c>, <c>/vsis3</c>, <c>/vsihttp</c>, …): the remote/indirection VSI
/// handlers are the SSRF / file-read reach, and an untrusted ingest never has a
/// legitimate reason to embed one — this catches a hostile <c>&lt;NetworkLink&gt;</c>
/// smuggled inside an otherwise-admissible KML/GML too.</item>
/// </list>
/// This is the belt-and-suspenders pre-check; the driver-skip + remote-VSI-disable
/// subprocess environment (<see cref="GdalRuntimeHardening"/>) is the second,
/// independent and authoritative control — <c>GDAL_SKIP</c> removes the entire
/// indirection/service driver family at registration, so even an indirection blob
/// this sniff does not specifically name cannot be opened by GDAL.
/// </summary>
internal static class GdalUntrustedInputGuard
{
    /// <summary>
    /// Element-open tokens of the GDAL indirection / OGC-service XML formats that
    /// dereference an external file or URL on open. Each is a specific, long root
    /// marker (≥ nine bytes) so scanning a decoded payload for it as an ASCII
    /// substring has a negligible false-positive rate on legitimate binary rasters or
    /// on the XML vector formats the worker legitimately imports (KML, GML). Matched
    /// case-insensitively.
    /// </summary>
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
    /// caller-safe <paramref name="reason"/> when the blob is a GDAL
    /// indirection/service XML format or embeds a virtual-filesystem reference.
    /// </summary>
    /// <param name="decoded">The base64-decoded input bytes.</param>
    /// <param name="reason">On rejection, a short reason suitable for a job-failure message.</param>
    /// <returns><see langword="true"/> when the blob is admissible; otherwise <see langword="false"/>.</returns>
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
        // codeql[cs/linq/missed-where] -- the predicate reads a ref-like span parameter.
        foreach (var candidate in IndirectionXmlMarkers)
        {
            if (IndexOfAsciiIgnoreCase(decoded, candidate) >= 0)
            {
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
        // codeql[cs/linq/missed-where] -- the predicate reads a ref-like span parameter.
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
