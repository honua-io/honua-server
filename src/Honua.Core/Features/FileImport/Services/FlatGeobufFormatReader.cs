// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using FlatGeobuf;
using FlatGeobuf.NTS;
using NetTopologySuite.Features;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Streaming FlatGeobuf format reader. Deserializes .fgb files into NTS features
/// using forward-only sequential reads for memory-efficient processing.
/// </summary>
internal static class FlatGeobufFormatReader
{
    /// <summary>
    /// Streams features from a FlatGeobuf file. Wraps the library's synchronous
    /// lazy <see cref="FeatureCollectionConversions.Deserialize(Stream, NetTopologySuite.Geometries.Envelope)"/>
    /// as an async enumerable with cancellation checks between features.
    /// Passes no bounding-box filter so the library performs a forward-only sequential scan.
    /// </summary>
    /// <remarks>
    /// The FlatGeobuf library requires <see cref="Stream.Position"/> and <see cref="Stream.Length"/>
    /// to navigate past the spatial index. When the input stream does not support seeking
    /// (e.g. <c>PrefixedReadStream</c> from the non-seekable CRS detection path or cloud-staged
    /// downloads), the content is spilled to a temporary file with
    /// <see cref="FileOptions.DeleteOnClose"/> to preserve constant-memory behavior for large files.
    /// </remarks>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The FlatGeobuf library accesses Stream.Position and Stream.Length to skip the
        // spatial index, so non-seekable streams must be materialized to a seekable stream.
        // A temp file (DeleteOnClose) is used instead of MemoryStream to avoid loading the
        // entire file into RAM on cloud-staged import paths.
        // NOTE: Current callers (StreamingFileImportService.ImportFileAsync / PreviewFileAsync)
        // spill non-seekable FlatGeobuf streams before calling this method, so this branch
        // is a defensive safety net for direct or future callers.
        Stream readable = stream;
        FileStream? tempFile = null;
        if (!stream.CanSeek)
        {
            tempFile = await ImportStreamHelper.SpillToSeekableTempAsync(stream, cancellationToken);
            readable = tempFile;
        }

        try
        {
            // Deserialize is lazy — features are materialized one at a time.
            // Passing null rect means no spatial filter: forward-only sequential scan.
            foreach (var feature in FeatureCollectionConversions.Deserialize(readable, null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return feature;
            }
        }
        finally
        {
            if (tempFile != null)
            {
                await tempFile.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Reads the feature count from a FlatGeobuf header. Returns null when the header
    /// is unreadable or the count is zero (which the FlatGeobuf spec uses for "unknown").
    /// The caller is responsible for resetting the stream position if needed.
    /// </summary>
    internal static int? ReadHeaderFeatureCount(Stream stream)
    {
        try
        {
            var header = Helpers.ReadHeader(stream);
            var count = header.FeaturesCount;
            return count > 0 ? (int)Math.Min(count, int.MaxValue) : null;
        }
        catch (Exception) // Malformed or truncated header
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the SRID and, when code-based resolution fails, the raw CRS WKT from a
    /// FlatGeobuf stream header. The caller can pass the WKT to
    /// <see cref="Honua.Core.Features.Infrastructure.Abstractions.ICrsDetectionService.DetectFromWktAsync"/>
    /// for authority-based SRID lookup.
    /// The caller is responsible for resetting the stream position if needed.
    /// </summary>
    internal static (int? Srid, string? CrsWkt) ReadCrsInfo(Stream stream)
    {
        try
        {
            var header = Helpers.ReadHeader(stream);
            var srid = ResolveSrid(header);
            if (srid.HasValue) return (srid, null);

            var wkt = ExtractCrsWkt(header);
            return (null, wkt);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Buffer-based variant of <see cref="ReadCrsInfo(Stream)"/> for non-seekable streams
    /// where the first 8 KB has been read into a buffer.
    /// </summary>
    internal static (int? Srid, string? CrsWkt) ReadCrsInfoFromHeader(byte[] headerBytes)
    {
        try
        {
            using var stream = new MemoryStream(headerBytes, writable: false);
            var header = Helpers.ReadHeader(stream);
            var srid = ResolveSrid(header);
            if (srid.HasValue) return (srid, null);

            var wkt = ExtractCrsWkt(header);
            return (null, wkt);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Extracts the raw WKT string from a parsed FlatGeobuf header CRS, if present.
    /// Returns null when the header has no CRS or the WKT field is empty.
    /// </summary>
    internal static string? ExtractCrsWkt(Header header)
    {
        var crs = header.Crs;
        if (crs is null) return null;
        var wkt = crs.Value.Wkt;
        return string.IsNullOrEmpty(wkt) ? null : wkt;
    }

    /// <summary>
    /// Resolves the SRID from a parsed FlatGeobuf header. Per the FlatGeobuf spec,
    /// an omitted <c>org</c> implies EPSG, so the numeric <c>code</c> is only trusted
    /// when the authority is absent or explicitly EPSG. Non-EPSG authorities
    /// (e.g. ESRI:102100) fall through to authority/codeString resolution or return null.
    /// </summary>
    internal static int? ResolveSrid(Header header)
    {
        var crs = header.Crs;
        if (crs is null)
            return null;

        var org = crs.Value.Org;
        var code = Helpers.GetCrsCode(header);

        // Per the FlatGeobuf spec, an omitted org implies EPSG.
        // Only trust the numeric code when the authority is absent or explicitly EPSG;
        // non-EPSG authorities (e.g. ESRI:102100) use a different code space.
        if (code > 0
            && (string.IsNullOrEmpty(org) || org.Equals("EPSG", StringComparison.OrdinalIgnoreCase)))
            return code;

        // Fall back to authority:codeString pairs for non-numeric or non-EPSG cases.
        var codeString = crs.Value.CodeString;

        if (string.IsNullOrEmpty(org) || string.IsNullOrEmpty(codeString))
            return null;

        // OGC:CRS84 is WGS 84 with lon/lat axis order — equivalent to EPSG:4326 in PostGIS.
        if (org.Equals("OGC", StringComparison.OrdinalIgnoreCase)
            && codeString.Equals("CRS84", StringComparison.OrdinalIgnoreCase))
            return 4326;

        // EPSG authority with a parseable codeString (e.g. Org="EPSG", CodeString="32632")
        if (org.Equals("EPSG", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(codeString, out var epsg) && epsg > 0)
            return epsg;

        return null;
    }
}
