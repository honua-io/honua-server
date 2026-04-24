// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Services;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Dbf.Fields;
using NetTopologySuite.IO.Esri.Shapefiles.Writers;
using NtsFeature = NetTopologySuite.Features.Feature;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Features.Export.Writers;

/// <summary>
/// Writes features as an ESRI Shapefile packaged in a ZIP archive.
/// Uses a scratch directory for intermediate files and streams the ZIP to output.
/// </summary>
internal static class ShapefileExportWriter
{
    private const int MaxDbfFieldNameLength = 10;

    public static async Task<ShapefileWriteResult> WriteAsync(
        Stream output,
        IAsyncEnumerable<Feature> features,
        FieldDefinition[] fields,
        GeometryType geometryType,
        string? prjWkt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (geometryType is GeometryType.GeometryCollection)
        {
            throw new InvalidOperationException(
                "Shapefile format does not support mixed geometry types (GeometryCollection).");
        }

        var scratchDir = Path.Combine(Path.GetTempPath(), "honua-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        try
        {
            var shpPath = Path.Combine(scratchDir, "export.shp");
            var warnings = new List<string>();
            var skippedNullGeometry = 0;

            // Build DBF field name mappings (unique and <=10 chars)
            var dbfFieldMap = BuildDbfFieldMap(fields, warnings);

            // Collect features to NTS feature list
            var ntsFeatures = new List<NtsFeature>();
            await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (feature.Geometry is null || feature.Geometry.Length == 0)
                {
                    skippedNullGeometry++;
                    continue;
                }

                var geometry = WkbReaderCache.Get().Read(feature.Geometry);
                geometry = NormalizeGeometry(geometry, geometryType);

                var attributes = new AttributesTable();
                foreach (var field in fields)
                {
                    if (dbfFieldMap.TryGetValue(field.Name, out var dbfName))
                    {
                        feature.Attributes.TryGetValue(field.Name, out var value);
                        attributes.Add(dbfName, NormalizeDbfValue(value, field.Type));
                    }
                }

                ntsFeatures.Add(new NtsFeature(geometry, attributes));
            }

            if (skippedNullGeometry > 0)
            {
                warnings.Add($"{skippedNullGeometry} feature(s) skipped due to null geometry.");
            }

            // Write shapefile using NTS — WriteAllFeatures auto-detects shape type from geometry
            Shapefile.WriteAllFeatures(ntsFeatures, shpPath);

            // Write .prj file if CRS WKT available
            if (!string.IsNullOrEmpty(prjWkt))
            {
                var prjPath = Path.ChangeExtension(shpPath, ".prj");
                await File.WriteAllTextAsync(prjPath, prjWkt, cancellationToken).ConfigureAwait(false);
            }

            // Create ZIP archive to temp file to avoid sync I/O on response body
            var zipPath = Path.Combine(scratchDir, "export.zip");
            {
                await using var zipStream = File.Create(zipPath);
                using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);
                foreach (var file in Directory.EnumerateFiles(scratchDir))
                {
                    if (file == zipPath) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await using var fileStream = File.OpenRead(file);
                    await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                }
            }

            // Stream ZIP file to output asynchronously
            await using var outputZip = File.OpenRead(zipPath);
            await outputZip.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

            return new ShapefileWriteResult(ntsFeatures.Count, skippedNullGeometry, warnings);
        }
        finally
        {
            try { Directory.Delete(scratchDir, recursive: true); }
            catch (Exception ex)
            {
                ExportLog.ScratchDirectoryCleanupFailed(logger, scratchDir, ex);
            }
        }
    }

    private static Dictionary<string, string> BuildDbfFieldMap(FieldDefinition[] fields, List<string> warnings)
    {
        var map = new Dictionary<string, string>(fields.Length);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            var dbfName = CreateUniqueDbfFieldName(field.Name, usedNames);

            if (dbfName != field.Name)
            {
                warnings.Add($"Field '{field.Name}' exported as DBF field '{dbfName}' (Shapefile 10-char unique-name limit).");
            }

            map[field.Name] = dbfName;
        }

        return map;
    }

    private static string CreateUniqueDbfFieldName(string name, HashSet<string> usedNames)
    {
        var baseName = name.Length <= MaxDbfFieldNameLength
            ? name
            : name[..MaxDbfFieldNameLength];

        if (usedNames.Add(baseName))
        {
            return baseName;
        }

        for (var i = 1; i < 10_000; i++)
        {
            var suffix = "_" + i.ToString(CultureInfo.InvariantCulture);
            var prefixLength = Math.Min(baseName.Length, MaxDbfFieldNameLength - suffix.Length);
            if (prefixLength <= 0)
            {
                throw new InvalidOperationException("Unable to generate a unique DBF field name within the 10-character limit.");
            }

            var candidate = string.Concat(baseName.AsSpan(0, prefixLength), suffix);
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to generate a unique DBF field name within the 10-character limit.");
    }

    private static Geometry NormalizeGeometry(Geometry geometry, GeometryType targetType)
    {
        return targetType switch
        {
            // Promote single → multi
            GeometryType.MultiPoint when geometry is Point p => new MultiPoint([p]),
            GeometryType.MultiLineString when geometry is LineString ls => new MultiLineString([ls]),
            GeometryType.MultiPolygon when geometry is Polygon pg => new MultiPolygon([pg]),
            // Preserve multipart line and polygon geometries. Shapefile polyline/polygon
            // records support multiple parts, so taking only the first part corrupts data.
            GeometryType.Point when geometry is MultiPoint =>
                throw new InvalidOperationException("Shapefile export cannot write MultiPoint geometry from a layer declared as Point without corrupting geometry type."),
            _ => geometry
        };
    }

    private static object? NormalizeDbfValue(object? value, FieldType fieldType)
    {
        if (value is null or DBNull)
            return null;

        return fieldType switch
        {
            FieldType.Boolean when value is bool b => b,
            FieldType.DateTime when value is DateTimeOffset dto => dto.DateTime,
            FieldType.Date when value is DateTimeOffset dto => dto.DateTime,
            _ => value
        };
    }
}

/// <summary>
/// Result of a Shapefile write operation.
/// </summary>
internal sealed record ShapefileWriteResult(int WrittenCount, int SkippedNullGeometry, List<string> Warnings);
