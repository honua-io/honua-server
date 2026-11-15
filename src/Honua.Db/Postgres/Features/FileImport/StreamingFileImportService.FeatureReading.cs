// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using NetTopologySuite.Features;
using FileGdb = Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private sealed record FeatureReadContext(Stream Source, SupportedFileFormat Format, ImportLoadMode LoadMode)
    {
        public required ImportRequest Request { get; init; }
        public CsvGeometryDiagnostics? CsvDiagnostics { get; init; }
        public WktGeometryDiagnostics? WktDiagnostics { get; init; }
        public string? ShapefilePath { get; init; }
        public string? FileGdbPath { get; init; }
    }

    private IAsyncEnumerable<IFeature> ReadImportFeatures(FeatureReadContext context, CancellationToken cancellationToken)
    {
        if (context.LoadMode == ImportLoadMode.CreateOnly && context.Format != SupportedFileFormat.GeoJson)
        {
            throw new NotSupportedException("Create-only imports require GeoJSON with complete input validation.");
        }

        var features = context.Format switch
        {
            SupportedFileFormat.GeoJson => ReadGeoJsonFeatures(context.Source, context.LoadMode, cancellationToken),
            SupportedFileFormat.GeoServicesJson => GeoServicesJsonFormatReader.ReadStreamingAsync(context.Source, cancellationToken),
            SupportedFileFormat.Wkb => WkbFormatReader.ReadStreamingAsync(context.Source, cancellationToken),
            SupportedFileFormat.Wkt => WktFormatReader.ReadStreamingAsync(context.Source, context.WktDiagnostics, cancellationToken),
            SupportedFileFormat.Kml => KmlFormatReader.ReadStreamingAsync(context.Source, cancellationToken),
            SupportedFileFormat.Gpx => GpxFormatReader.ReadStreamingAsync(context.Source, cancellationToken),
            SupportedFileFormat.Gml => GmlFormatReader.ReadStreamingAsync(context.Source, cancellationToken),
            SupportedFileFormat.Csv => CsvFormatReader.ReadStreamingAsync(context.Source, delimiterOverride: null, context.CsvDiagnostics, context.Request.CsvOptions, cancellationToken),
            SupportedFileFormat.FlatGeobuf => FlatGeobufFormatReader.ReadStreamingAsync(context.Source, cancellationToken),
            SupportedFileFormat.Shapefile => ReadShapefileStreamingAsync(context.ShapefilePath!, cancellationToken),
            SupportedFileFormat.GeoPackage => ReadGeoPackageStreamingAsync(context.Source, cancellationToken),
            SupportedFileFormat.FileGdb => FileGdb.FileGdbReader.ReadStreamingAsync(context.FileGdbPath!, cancellationToken),
            SupportedFileFormat.GeoParquet => GeoParquetReader.ReadStreamingAsync(context.Source, _limits, cancellationToken),
            _ => throw new NotSupportedException($"Streaming not supported for format: {context.Format}")
        };
        return context.LoadMode == ImportLoadMode.CreateOnly
            ? EnforceCreateOnlyFeatureLimit(features, cancellationToken)
            : features;
    }
}
