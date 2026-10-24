// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Db.Postgres.Features.Migration;
using Honua.Db.Postgres.Features.FileImport;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    /// <summary>
    /// Detect CRS from stream without loading entire file.
    /// </summary>
    private async Task<int?> DetectCrsStreamingAsync(
        Stream stream,
        SupportedFileFormat format,
        CancellationToken cancellationToken)
    {
        try
        {
            return format switch
            {
                SupportedFileFormat.GeoJson => await DetectGeoJsonSridAsync(stream, cancellationToken),
                SupportedFileFormat.EsriJson => await EsriJsonFormatReader.TryDetectSridAsync(stream, cancellationToken) ?? 4326,
                SupportedFileFormat.Kml => 4326,
                SupportedFileFormat.Gpx => 4326,
                // A PostGIS CSV export carries the CRS on each geometry (EWKT/EWKB). Honour
                // that SRID instead of assuming 4326; lon/lat and plain WKT keep the default (#6171).
                SupportedFileFormat.Csv => await CsvFormatReader.TryDetectGeometrySridAsync(stream, cancellationToken) ?? 4326,
                SupportedFileFormat.Gml => await GmlFormatReader.TryDetectSridAsync(stream, cancellationToken),
                SupportedFileFormat.Wkt => await DetectWktSridAsync(stream, cancellationToken),
                SupportedFileFormat.GeoPackage => await DetectGeoPackageSridAsync(stream, cancellationToken),
                SupportedFileFormat.FlatGeobuf => await DetectFlatGeobufCrsAsync(stream, cancellationToken),
                SupportedFileFormat.FileGdb => null, // CRS resolved during preparation via ResolveFileGdbSridAsync
                _ => null
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally generic: best-effort CRS auto-detection — on failure, log and return null
            // so the caller falls back to an explicit source SRID or the format's default rather than
            // failing the import.
            ImportLog.CrsDetectionFailed(_logger, ex, format);
            return null;
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = 0;
        }
    }

    /// <summary>
    /// Resolves the source SRID of discovered FileGDB feature classes. A layer whose WKT carries
    /// an EPSG authority (or a plain WGS84/NAD83 geographic name) already has an SRID; otherwise the
    /// layer's ESRI WKT is resolved through <see cref="ICrsDetectionService"/> exactly like a
    /// shapefile <c>.prj</c> sidecar (ESRI projected-name table, then spatial_ref_sys WKT match).
    /// ArcGIS writes ESRI WKT1 without AUTHORITY nodes for every State Plane / UTM / Web Mercator
    /// class, so skipping this step silently stored projected coordinates as EPSG:4326 (#6114).
    /// Returns null when nothing resolves so the caller reports <c>SourceSridRequired</c>.
    /// </summary>
    private async Task<int?> ResolveFileGdbSridAsync(
        Honua.Core.Features.FileImport.Services.FileGdb.FileGdbReader.FileGdbLayerInfo[] layers,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < layers.Length; i++)
        {
            if (layers[i].Srid > 0)
            {
                return layers[i].Srid;
            }
        }

        for (var i = 0; i < layers.Length; i++)
        {
            var wkt = layers[i].SpatialReferenceWkt;
            if (string.IsNullOrWhiteSpace(wkt))
            {
                continue;
            }

            try
            {
                var resolved = await _crsDetectionService.DetectFromPrjAsync(wkt, cancellationToken);
                if (resolved is > 0)
                {
                    return resolved;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // Best-effort like every other format's CRS detection: an unresolvable WKT falls
                // through to an explicit SourceSrid or a SourceSridRequired failure.
                ImportLog.CrsDetectionFailed(_logger, ex, SupportedFileFormat.FileGdb);
            }
        }

        return null;
    }

    /// <summary>
    /// Detects the SRID from a FlatGeobuf stream by reading the binary header.
    /// Tries code/codeString/authority resolution first, then falls back to WKT
    /// parsing via <see cref="ICrsDetectionService.DetectFromWktAsync"/> when the
    /// header embeds CRS as WKT only.
    /// </summary>
    private async Task<int?> DetectFlatGeobufCrsAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var (srid, crsWkt) = FlatGeobufFormatReader.ReadCrsInfo(stream);
        if (srid.HasValue) return srid;
        if (!string.IsNullOrEmpty(crsWkt))
            return await _crsDetectionService.DetectFromWktAsync(crsWkt, cancellationToken);
        return null;
    }

    private static async Task<int?> DetectGeoJsonSridAsync(Stream stream, CancellationToken cancellationToken)
    {
        var detected = await StreamingGeoJsonReader.DetectCrsAsync(stream, cancellationToken);
        return detected ?? 4326;
    }

    private static async Task<byte[]?> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[CrsDetectionHeaderSize];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (bytesRead <= 0)
        {
            return null;
        }

        if (bytesRead == buffer.Length)
        {
            return buffer;
        }

        Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    private async Task<int?> DetectCrsFromHeaderAsync(
        byte[] header,
        SupportedFileFormat format,
        CancellationToken cancellationToken)
    {
        await using var headerStream = new MemoryStream(header, writable: false);

        return format switch
        {
            SupportedFileFormat.GeoJson => await DetectGeoJsonSridAsync(headerStream, cancellationToken),
            SupportedFileFormat.EsriJson => await EsriJsonFormatReader.TryDetectSridAsync(headerStream, cancellationToken) ?? 4326,
            SupportedFileFormat.Kml => 4326,
            SupportedFileFormat.Gpx => 4326,
            SupportedFileFormat.Csv => await CsvFormatReader.TryDetectGeometrySridAsync(headerStream, cancellationToken) ?? 4326,
            SupportedFileFormat.Gml => await GmlFormatReader.TryDetectSridAsync(headerStream, cancellationToken),
            SupportedFileFormat.Wkt => await DetectWktSridAsync(headerStream, cancellationToken),
            // Defensive: currently unreachable — FlatGeobuf non-seekable paths spill to
            // a seekable temp file before reaching here (see the dedicated FlatGeobuf branch above).
            SupportedFileFormat.FlatGeobuf => await DetectFlatGeobufCrsFromHeaderAsync(header, cancellationToken),
            _ => null
        };
    }

    /// <summary>
    /// Defensive buffer-based FlatGeobuf CRS detection with WKT fallback.
    /// Currently unreachable — non-seekable FlatGeobuf streams are spilled to
    /// seekable temp files before the header-buffer path.
    /// </summary>
    private async Task<int?> DetectFlatGeobufCrsFromHeaderAsync(byte[] headerBytes, CancellationToken cancellationToken = default)
    {
        var (srid, crsWkt) = FlatGeobufFormatReader.ReadCrsInfoFromHeader(headerBytes);
        if (srid.HasValue) return srid;
        if (!string.IsNullOrEmpty(crsWkt))
            return await _crsDetectionService.DetectFromWktAsync(crsWkt, cancellationToken);
        return null;
    }

    private static async Task<int?> DetectWktSridAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[CrsDetectionHeaderSize];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (bytesRead <= 0)
        {
            return null;
        }

        var header = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        var match = _wktSridRegex.Match(header);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var srid))
        {
            return null;
        }

        return srid;
    }

    private async Task<int?> DetectGeoPackageSridAsync(Stream stream, CancellationToken cancellationToken)
    {
        GeoPackageScratch? scratch = null;
        try
        {
            scratch = await PrepareGeoPackageScratchAsync(stream, cancellationToken);
            var layers = await GetGeoPackageLayersAsync(scratch.FilePath, cancellationToken);
            return layers.Count > 0 ? layers[0].Srid : null;
        }
        finally
        {
            CleanupGeoPackageScratch(scratch);
        }
    }
}
