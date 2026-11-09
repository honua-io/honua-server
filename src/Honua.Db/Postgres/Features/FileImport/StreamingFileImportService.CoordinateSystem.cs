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
        var formatDefined = format is SupportedFileFormat.Kml or SupportedFileFormat.Gpx;
        return ImportCoordinateSystem.FromDetection(srid, srid.HasValue && !formatDefined, formatDefined);
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
            var srid = await DetectCrsStreamingAsync(stream, format, cancellationToken);
            return FromDetectedSrid(format, srid);
        }
        finally
        {
            if (stream.CanSeek) stream.Position = 0;
        }
    }
}
