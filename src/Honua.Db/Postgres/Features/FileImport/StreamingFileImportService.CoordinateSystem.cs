// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static ImportCoordinateSystem FromDetectedSrid(SupportedFileFormat format, int? srid)
    {
        if (format is SupportedFileFormat.Kml or SupportedFileFormat.Gpx)
        {
            return ImportCoordinateSystem.FromDetection(srid, ImportCrsProvenance.FormatDefined);
        }
        return ImportCoordinateSystem.FromDetection(
            srid, srid.HasValue ? ImportCrsProvenance.FileMetadata : ImportCrsProvenance.Unknown);
    }

    private async Task<ImportCoordinateSystem> InspectFlatGeobufCoordinateSystemAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var (headerSrid, crsWkt) = FlatGeobufFormatReader.ReadCrsInfo(stream);
        if (headerSrid.HasValue)
        {
            return ImportCoordinateSystem.FromDetection(headerSrid, ImportCrsProvenance.FileMetadata);
        }
        if (string.IsNullOrEmpty(crsWkt))
        {
            return ImportCoordinateSystem.FromDetection(null, ImportCrsProvenance.Unknown);
        }
        return ImportCoordinateSystem.FromDetection(
            await ResolveDeclaredWktSridAsync(crsWkt, SupportedFileFormat.FlatGeobuf, cancellationToken),
            ImportCrsProvenance.FileMetadata);
    }

    private async Task<int?> ResolveDeclaredWktSridAsync(
        string crsWkt, SupportedFileFormat format, CancellationToken cancellationToken)
    {
        try
        {
            return await _crsDetectionService.DetectFromWktAsync(crsWkt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            ImportLog.CrsDetectionFailed(_logger, ex, format);
            return null;
        }
    }

    private async Task<ImportCoordinateSystem> InspectSourceCoordinateSystemAsync(
        Stream stream, SupportedFileFormat format, CancellationToken cancellationToken)
    {
        try
        {
            if (format == SupportedFileFormat.GeoJson)
            {
                return await StreamingGeoJsonReader.InspectCoordinateSystemAsync(stream, cancellationToken);
            }
            if (format == SupportedFileFormat.Gml)
            {
                return await GmlFormatReader.InspectCoordinateSystemAsync(stream, cancellationToken);
            }
            if (format == SupportedFileFormat.GeoServicesJson)
            {
                return await GeoServicesJsonFormatReader.InspectCoordinateSystemAsync(stream, cancellationToken);
            }
            if (format == SupportedFileFormat.FlatGeobuf)
            {
                return await InspectFlatGeobufCoordinateSystemAsync(stream, cancellationToken);
            }
            var srid = await DetectCrsStreamingAsync(stream, format, cancellationToken);
            return FromDetectedSrid(format, srid);
        }
        finally
        {
            if (stream.CanSeek) stream.Position = 0;
        }
    }
    private static ImportValidationIssue[] SourceSridRequiredIssues(string message) =>
        [ImportValidationIssue.Create(
            ImportValidationErrorCodes.SourceSridRequired,
            message,
            field: nameof(ImportRequest.SourceSrid))];

    private static ImportCoordinateSystem GetShapefileCoordinateSystem(string shpPath, int? detectedSrid)
    {
        var prjDeclared = Directory.EnumerateFiles(Path.GetDirectoryName(shpPath)!)
            .Any(path => Path.GetExtension(path).Equals(".prj", StringComparison.OrdinalIgnoreCase));
        return ImportCoordinateSystem.FromDetection(
            detectedSrid, prjDeclared ? ImportCrsProvenance.FileMetadata : ImportCrsProvenance.Unknown);
    }
}
