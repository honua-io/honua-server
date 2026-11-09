// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static async Task<ImportCoordinateSystem> InspectCoordinateSystemAsync(
        Stream stream, SupportedFileFormat format, int? detectedSrid, CancellationToken cancellationToken)
    {
        var formatDefined = format is SupportedFileFormat.Kml or SupportedFileFormat.Gpx;
        var declared = detectedSrid.HasValue && !formatDefined;
        if (format == SupportedFileFormat.GeoJson)
        {
            var detection = await StreamingGeoJsonReader.DetectCrsMetadataAsync(stream, cancellationToken);
            declared = detection.Declared;
            formatDefined = !declared;
        }
        return new ImportCoordinateSystem
        {
            DetectedSrid = detectedSrid,
            SourceCrsDeclared = declared,
            DetectionMethod = formatDefined ? "format-defined" : declared ? "file-metadata" : "unknown",
            EffectiveSourceSrid = detectedSrid,
            StoredSrid = detectedSrid,
            RequiresSourceOverride = !detectedSrid.HasValue
        };
    }
}
