// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Shared.Models;
using NetTopologySuite.Features;
using FileGdb = Honua.Core.Features.FileImport.Services.FileGdb;
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
    /// <inheritdoc/>
    public async Task<FilePreview> PreviewFileAsync(
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var format = DetectFormat(fileName);

        if (!format.HasValue)
        {
            throw new NotSupportedException("Unsupported file format: " + Path.GetExtension(fileName));
        }

        ShapefileScratch? shapefileScratch = null;
        GeoPackageScratch? geoPackageScratch = null;
        KmzScratch? kmzScratch = null;
        FileGdbScratch? fileGdbScratch = null;
        GeoPackageLayerInfo? previewGeoPackageLayer = null;
        FileGdb.FileGdbReader.FileGdbLayerInfo? previewFileGdbLayer = null;
        GeoParquetScratch? geoParquetScratch = null;
        long? geoParquetTotalRows = null;
        FileStream? fgbTempStream = null;
        string[] warnings = [];
        Stream? kmzStream = null;
        try
        {
            int? detectedSrid;
            string[] availableLayers = [];

            if (format.Value == SupportedFileFormat.GeoPackage)
            {
                geoPackageScratch = await PrepareGeoPackageScratchAsync(fileStream, cancellationToken);
                var layers = await GetGeoPackageLayersAsync(geoPackageScratch.FilePath, cancellationToken);
                if (layers.Count == 0)
                {
                    throw new InvalidDataException("GeoPackage does not contain any feature layers.");
                }

                availableLayers = layers.Select(layer => layer.TableName).ToArray();
                previewGeoPackageLayer = layers[0];
                detectedSrid = layers[0].Srid;
            }
            else if (format.Value == SupportedFileFormat.Shapefile)
            {
                if (!IsZipFileName(fileName))
                {
                    throw new NotSupportedException("Shapefile preview requires a .zip containing .shp and .dbf files.");
                }

                shapefileScratch = await PrepareShapefileScratchAsync(fileStream, fileName, cancellationToken);
                detectedSrid = await _crsDetectionService.DetectFromShapefilePrjAsync(shapefileScratch.ShpPath);
            }
            else if (format.Value == SupportedFileFormat.Kml && IsKmzFileName(fileName))
            {
                kmzScratch = await PrepareKmzScratchAsync(fileStream, cancellationToken);
                kmzStream = new FileStream(kmzScratch.KmlPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = _limits.StreamBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                fileStream = kmzStream;
                detectedSrid = SpatialConstants.DefaultSrid;
            }
            else if (format.Value == SupportedFileFormat.FileGdb)
            {
                fileGdbScratch = await PrepareFileGdbScratchAsync(fileStream, cancellationToken);
                var layers = FileGdb.FileGdbReader.DiscoverLayers(fileGdbScratch.GdbPath);
                availableLayers = layers.Select(layer => layer.Name).ToArray();
                previewFileGdbLayer = layers.Length > 0 ? layers[0] : null;
                detectedSrid = layers.FirstOrDefault(layer => layer.Srid > 0).Srid;
                if (detectedSrid <= 0)
                {
                    detectedSrid = FileGdb.FileGdbReader.DetectSrid(fileGdbScratch.GdbPath);
                }

                warnings = FileGdb.FileGdbAdvancedConstructs.DetectWarnings(fileGdbScratch.GdbPath);
                if (layers.Length > 1)
                {
                    warnings = warnings
                        .Append(FileGdb.FileGdbReader.BuildMultiLayerImportMessage(layers))
                        .ToArray();
                }
            }
            else if (format.Value == SupportedFileFormat.GeoParquet)
            {
                geoParquetScratch = await PrepareGeoParquetScratchAsync(fileStream, cancellationToken);
                var scratchStream = geoParquetScratch.Stream;
                var parquetMeta = await GeoParquetReader.ExtractMetadataAsync(scratchStream, cancellationToken);
                detectedSrid = parquetMeta.Srid;
                warnings = parquetMeta.Warnings;
                geoParquetTotalRows = parquetMeta.TotalRowCount;

                // Reject files with any oversized row group — consistent with import path
                if (parquetMeta.MaxRowGroupRowCount > GeoParquetReader.MaxRowsPerRowGroup)
                {
                    throw new InvalidDataException(
                        GeoParquetReader.BuildLargeRowGroupMessage(
                            parquetMeta.MaxRowGroupRowCount, parquetMeta.RowGroupCount));
                }

                // Hard-reject non-WKB encoding — consistent with the import path
                // (ImportFileAsync returns CreateFailure) and the documented contract
                // ("Non-WKB encodings are rejected" in CONTROL_PLANE_API.md).
                if (!parquetMeta.IsWkbEncoding)
                {
                    throw new NotSupportedException(
                        GeoParquetReader.UnsupportedEncodingMessage);
                }

                fileStream = scratchStream;
            }
            else if (format.Value == SupportedFileFormat.FlatGeobuf && !fileStream.CanSeek)
            {
                // FlatGeobuf binary headers can exceed the 8 KB CRS-detection buffer when
                // the schema has many columns. Spill to a seekable temp file so full-header
                // CRS detection and the library's Deserialize (which requires seeking) work.
                fgbTempStream = await SpillToSeekableTempAsync(fileStream, cancellationToken);
                fileStream = fgbTempStream;
                ImportLog.SpilledToTempFile(_logger, fgbTempStream.Length);
                detectedSrid = await DetectCrsStreamingAsync(fileStream, format.Value, cancellationToken);
            }
            else
            {
                if (!fileStream.CanSeek)
                {
                    var header = await ReadHeaderAsync(fileStream, cancellationToken);
                    detectedSrid = header == null
                        ? null
                        : await DetectCrsFromHeaderAsync(header, format.Value, cancellationToken);
                    if (header != null && header.Length > 0)
                    {
                        fileStream = new PrefixedReadStream(header, fileStream);
                    }
                }
                else
                {
                    // Detect CRS using streaming
                    detectedSrid = await DetectCrsStreamingAsync(fileStream, format.Value, cancellationToken);
                }
            }

            // FlatGeobuf embeds the total feature count in its binary header.
            // Read it before enumeration so the preview can report the true total
            // even when the file exceeds the sample cap.
            int? headerFeatureCount = null;
            if (format.Value == SupportedFileFormat.FlatGeobuf && fileStream.CanSeek)
            {
                // CRS detection may have advanced position past the header;
                // reset so ReadHeaderFeatureCount reads from the magic bytes.
                fileStream.Position = 0;
                headerFeatureCount = FlatGeobufFormatReader.ReadHeaderFeatureCount(fileStream);
                fileStream.Position = 0;
            }

            // Stream features but only collect up to the limit
            var features = new List<IFeature>();
            var featureStream = format.Value switch
            {
                SupportedFileFormat.GeoJson => _geoJsonReader.ReadFeaturesAsync(fileStream, cancellationToken),
                SupportedFileFormat.Wkt => WktFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Kml => KmlFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Gpx => GpxFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Csv => CsvFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.FlatGeobuf => FlatGeobufFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Shapefile => ReadShapefileStreamingAsync(shapefileScratch!.ShpPath, cancellationToken),
                SupportedFileFormat.GeoPackage => ReadGeoPackageLayerAsync(
                    geoPackageScratch!.FilePath,
                    previewGeoPackageLayer ?? throw new InvalidOperationException("GeoPackage preview layer was not prepared."),
                    cancellationToken),
                SupportedFileFormat.FileGdb => previewFileGdbLayer.HasValue
                    ? FileGdb.FileGdbReader.ReadLayerStreamingAsync(fileGdbScratch!.GdbPath, previewFileGdbLayer.Value, cancellationToken)
                    : EmptyFeatureStream(cancellationToken),
                SupportedFileFormat.GeoParquet => GeoParquetReader.ReadStreamingAsync(fileStream, cancellationToken),
                _ => throw new NotSupportedException($"Preview not supported for format: {format}")
            };

            // FlatGeobuf files serialized by some writers (including NTS) set FeaturesCount=0
            // (meaning "unknown"). When the header count is unavailable, continue iterating
            // past the sample cap to count features — but only up to MaxPreviewCountScan to
            // prevent unbounded scans of very large files.
            var needsFullCount = format.Value == SupportedFileFormat.FlatGeobuf
                && !headerFeatureCount.HasValue;
            var totalStreamedCount = 0;

            await foreach (var feature in featureStream.WithCancellation(cancellationToken))
            {
                // GeoParquet: skip null-geometry rows from preview samples, matching
                // the import path's skip behavior (ImportStreamingAsync).
                if (format.Value == SupportedFileFormat.GeoParquet && feature.Geometry == null)
                {
                    continue;
                }

                if (needsFullCount && totalStreamedCount >= _limits.MaxPreviewCountScan)
                {
                    break;
                }

                totalStreamedCount++;
                if (features.Count < _limits.MaxPreviewFeatures)
                {
                    features.Add(feature);
                }
                else if (!needsFullCount)
                {
                    break;
                }
            }

            var sampleProperties = new Dictionary<string, object?>();
            var firstFeature = features.FirstOrDefault();
            if (firstFeature?.Attributes is not null)
            {
                var names = firstFeature.Attributes.GetNames();
                var values = firstFeature.Attributes.GetValues();
                sampleProperties = names.Zip(values).ToDictionary(
                    pair => pair.First,
                    pair => pair.Second is byte[] bytes
                        ? (object?)Convert.ToBase64String(bytes)
                        : pair.Second);
            }

            return new FilePreview
            {
                Format = format.Value,
                TotalFeatureCount = geoParquetTotalRows.HasValue
                    ? (int)Math.Min(geoParquetTotalRows.Value, int.MaxValue)
                    : headerFeatureCount ?? (needsFullCount ? totalStreamedCount : features.Count),
                DetectedSrid = detectedSrid,
                SampleProperties = sampleProperties,
                AvailableLayers = availableLayers,
                Warnings = warnings
            };
        }
        finally
        {
            if (kmzStream != null)
            {
                await kmzStream.DisposeAsync();
            }

            if (fgbTempStream != null)
            {
                await fgbTempStream.DisposeAsync();
            }

            CleanupShapefileScratch(shapefileScratch);
            CleanupGeoPackageScratch(geoPackageScratch);
            CleanupKmzScratch(kmzScratch);
            CleanupFileGdbScratch(fileGdbScratch);
            CleanupGeoParquetScratch(geoParquetScratch);
        }
    }
}
