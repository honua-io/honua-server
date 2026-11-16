// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

namespace Honua.Core.Features.FileImport.Services;

internal static class FlatGeobufFormatReader
{
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

    internal static int? ReadHeaderFeatureCount(Stream stream)
    {
        try
        {
            var header = Helpers.ReadHeader(stream);
            var count = header.FeaturesCount;
            return count > 0 ? (int)Math.Min(count, int.MaxValue) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) // Malformed or truncated header
        {
            return null;
        }
    }

    internal static (int? Srid, string? CrsWkt, bool Declared) ReadCrsInfo(Stream stream)
    {
        try
        {
            var header = Helpers.ReadHeader(stream);
            var srid = ResolveSrid(header);
            if (srid.HasValue) return (srid, null, true);

            var wkt = ExtractCrsWkt(header);
            return (null, wkt, header.Crs.HasValue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, null, false);
        }
    }

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, null);
        }
    }

    internal static string? ExtractCrsWkt(Header header)
    {
        var crs = header.Crs;
        if (crs is null) return null;
        var wkt = crs.Value.Wkt;
        return string.IsNullOrEmpty(wkt) ? null : wkt;
    }

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
