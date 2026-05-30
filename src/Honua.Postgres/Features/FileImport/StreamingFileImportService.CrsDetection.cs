// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.FileImport;

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
                SupportedFileFormat.Kml => 4326,
                SupportedFileFormat.Gpx => 4326,
                SupportedFileFormat.Csv => 4326,
                SupportedFileFormat.Wkt => await DetectWktSridAsync(stream, cancellationToken),
                SupportedFileFormat.GeoPackage => await DetectGeoPackageSridAsync(stream, cancellationToken),
                SupportedFileFormat.FlatGeobuf => await DetectFlatGeobufCrsAsync(stream),
                SupportedFileFormat.FileGdb => null, // CRS detected during preparation via FileGdbReader.DetectSrid
                _ => null
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = 0;
        }
    }

    /// <summary>
    /// Detects the SRID from a FlatGeobuf stream by reading the binary header.
    /// Tries code/codeString/authority resolution first, then falls back to WKT
    /// parsing via <see cref="ICrsDetectionService.DetectFromWktAsync"/> when the
    /// header embeds CRS as WKT only.
    /// </summary>
    private async Task<int?> DetectFlatGeobufCrsAsync(Stream stream)
    {
        var (srid, crsWkt) = FlatGeobufFormatReader.ReadCrsInfo(stream);
        if (srid.HasValue) return srid;
        if (!string.IsNullOrEmpty(crsWkt))
            return await _crsDetectionService.DetectFromWktAsync(crsWkt);
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
            SupportedFileFormat.Kml => 4326,
            SupportedFileFormat.Gpx => 4326,
            SupportedFileFormat.Csv => 4326,
            SupportedFileFormat.Wkt => await DetectWktSridAsync(headerStream, cancellationToken),
            // Defensive: currently unreachable — FlatGeobuf non-seekable paths spill to
            // a seekable temp file before reaching here (see the dedicated FlatGeobuf branch above).
            SupportedFileFormat.FlatGeobuf => await DetectFlatGeobufCrsFromHeaderAsync(header),
            _ => null
        };
    }

    /// <summary>
    /// Defensive buffer-based FlatGeobuf CRS detection with WKT fallback.
    /// Currently unreachable — non-seekable FlatGeobuf streams are spilled to
    /// seekable temp files before the header-buffer path.
    /// </summary>
    private async Task<int?> DetectFlatGeobufCrsFromHeaderAsync(byte[] headerBytes)
    {
        var (srid, crsWkt) = FlatGeobufFormatReader.ReadCrsInfoFromHeader(headerBytes);
        if (srid.HasValue) return srid;
        if (!string.IsNullOrEmpty(crsWkt))
            return await _crsDetectionService.DetectFromWktAsync(crsWkt);
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
