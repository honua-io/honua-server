// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using System.Globalization;
using System.Collections.Immutable;
using Honua.Infrastructure.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Dbf.Fields;
using NetTopologySuite.IO.Esri.Dbf;
using NetTopologySuite.IO.Esri.Shapefiles.Writers;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Io.Export.Writers;

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
        ExportField[] fields,
        ExportGeometryType geometryType,
        string? prjWkt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (geometryType is ExportGeometryType.GeometryCollection or ExportGeometryType.Mixed)
        {
            throw new InvalidOperationException(
                "Shapefile format does not support mixed geometry types (GeometryCollection).");
        }

        // "honua-export" is a compile-time relative literal and Guid.NewGuid().ToString("N")
        // is a fixed-format hex string (no separators, never rooted), so this combine cannot
        // silently drop Path.GetTempPath().
        var scratchDir = Path.Join(Path.GetTempPath(), "honua-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        try
        {
            // "export.shp" is a compile-time relative literal, so it cannot be rooted.
            var shpPath = Path.Join(scratchDir, "export.shp");
            var warnings = new List<string>();


            // Build DBF field name mappings (unique and <=10 chars)
            var dbfFieldMap = BuildDbfFieldMap(fields, warnings);
            var dbfFields = BuildDbfFields(fields, dbfFieldMap);

            var writtenCount = 0;
            await using var enumerator = features.GetAsyncEnumerator(cancellationToken);

            // The ESRI shapefile header encodes a single shape type for the whole
            // file, including whether records carry Z/M ordinates — it cannot be
            // changed once records start writing. The layer schema does not carry
            // Z/M presence, so peek the first feature that has a geometry and probe
            // its ordinates to choose the matching Z/M shape-type variant; otherwise
            // 3D input is silently flattened to 2D on export (#2744).
            Feature? firstFeature = null;
            Geometry? firstGeometry = null;
            // Keep leading unlocated rows on disk while determining the shape dimensions.
            // A DBF spool preserves their fields and order without unbounded memory buffering.
            var prefixDirectory = Path.Join(scratchDir, "prefix");
            Directory.CreateDirectory(prefixDirectory);
            var prefixPath = Path.Join(prefixDirectory, "null-rows.dbf");
            DbfWriter? prefixWriter = null;
            try
            {
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    var candidate = enumerator.Current;
                    if (candidate.Geometry is null || candidate.Geometry.Length == 0)
                    {
                        prefixWriter ??= new DbfWriter(prefixPath, dbfFields, Encoding.UTF8);
                        SetDbfValues(prefixWriter.Fields, fields, dbfFieldMap, candidate.Attributes);
                        prefixWriter.Write();
                        continue;
                    }

                    firstFeature = candidate;
                    firstGeometry = WkbReaderCache.Get().Read(candidate.Geometry);
                    break;
                }
            }
            finally
            {
                prefixWriter?.Dispose();
            }

            var (hasZ, hasM) = DetectZM(firstGeometry);
            var options = new ShapefileWriterOptions(MapShapeType(geometryType, hasZ, hasM), dbfFields)
            {
                Encoding = Encoding.UTF8
            };
            // The NTS shapefile API requires an empty geometry of the file's shape
            // family to emit a Null Shape record; assigning null throws in its setter.
            var nullShape = CreateNullShapeGeometry(geometryType);
            var ordinateLayout = new ShapeOrdinateOperation(hasZ, hasM || hasZ);
            using (var shpWriter = Shapefile.OpenWrite(shpPath, options))
            {
                if (File.Exists(prefixPath))
                {
                    using var prefixReader = new DbfReader(prefixPath, Encoding.UTF8);
                    while (prefixReader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        shpWriter.Geometry = nullShape;
                        foreach (var field in prefixReader.Fields)
                        {
                            shpWriter.Fields[field.Name].Value = field.Value;
                        }

                        shpWriter.Write();
                        writtenCount++;
                    }
                }

                if (firstFeature.HasValue && firstGeometry is not null)
                {
                    shpWriter.Geometry = new GeometryEditor(firstGeometry.Factory).Edit(
                        NormalizeGeometry(firstGeometry, geometryType), ordinateLayout);
                    SetDbfValues(shpWriter.Fields, fields, dbfFieldMap, firstFeature.Value.Attributes);
                    shpWriter.Write();
                    writtenCount++;
                }

                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    var feature = enumerator.Current;
                    var geometry = feature.Geometry is { Length: > 0 }
                        ? WkbReaderCache.Get().Read(feature.Geometry)
                        : null;
                    shpWriter.Geometry = geometry is null ? nullShape : new GeometryEditor(geometry.Factory).Edit(
                        NormalizeGeometry(geometry, geometryType), ordinateLayout);
                    SetDbfValues(shpWriter.Fields, fields, dbfFieldMap, feature.Attributes);
                    shpWriter.Write();
                    writtenCount++;
                }
            }

            Directory.Delete(prefixDirectory, recursive: true);
            if (warnings.Count > 0)
            {
                await File.WriteAllLinesAsync(Path.Join(scratchDir, "export-warnings.txt"),
                    warnings, cancellationToken).ConfigureAwait(false);
            }

            // Write .prj file if CRS WKT available
            if (!string.IsNullOrEmpty(prjWkt))
            {
                var prjPath = Path.ChangeExtension(shpPath, ".prj");
                await File.WriteAllTextAsync(prjPath, prjWkt, cancellationToken).ConfigureAwait(false);
            }

            // Create ZIP archive to temp file to avoid sync I/O on response body.
            // "export.zip" is a compile-time relative literal, so it cannot be rooted.
            var zipPath = Path.Join(scratchDir, "export.zip");
            {
                await using var zipStream = File.Create(zipPath);
                using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);
                foreach (var file in Directory.EnumerateFiles(scratchDir).Where(file => file != zipPath))
                {
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

            return new ShapefileWriteResult(writtenCount, 0, warnings);
        }
        finally
        {
            // Intentionally generic: best-effort scratch-directory cleanup that must not
            // mask the write result (success or failure) above it; log and move on.
            try { Directory.Delete(scratchDir, recursive: true); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ExportLog.ScratchDirectoryCleanupFailed(logger, scratchDir, ex);
            }
        }
    }

    private static Dictionary<string, string> BuildDbfFieldMap(ExportField[] fields, List<string> warnings)
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

    private static DbfField[] BuildDbfFields(ExportField[] fields, Dictionary<string, string> dbfFieldMap)
    {
        var dbfFields = new List<DbfField>(fields.Length);

        foreach (var field in fields)
        {
            if (!dbfFieldMap.TryGetValue(field.Name, out var dbfName))
            {
                continue;
            }

            switch (field.Type)
            {
                case ExportFieldType.Integer:
                    dbfFields.AddNumericInt32Field(dbfName);
                    break;
                case ExportFieldType.BigInteger:
                    dbfFields.AddNumericInt64Field(dbfName);
                    break;
                case ExportFieldType.Double:
                    dbfFields.AddNumericDoubleField(dbfName);
                    break;
                case ExportFieldType.Float:
                    dbfFields.AddFloatField(dbfName);
                    break;
                case ExportFieldType.Boolean:
                    dbfFields.AddLogicalField(dbfName);
                    break;
                case ExportFieldType.Date:
                case ExportFieldType.DateTime:
                    dbfFields.AddDateField(dbfName);
                    break;
                default:
                    dbfFields.AddCharacterField(dbfName, length: 254);
                    break;
            }
        }

        return dbfFields.ToArray();
    }

    private static void SetDbfValues(
        DbfFieldCollection writerFields,
        ExportField[] fields,
        Dictionary<string, string> dbfFieldMap,
        ImmutableDictionary<string, object?> attributes)
    {
        foreach (var field in fields)
        {
            if (!dbfFieldMap.TryGetValue(field.Name, out var dbfName))
            {
                continue;
            }

            var dbfField = writerFields[dbfName];
            if (dbfField is null)
            {
                continue;
            }

            attributes.TryGetValue(field.Name, out var value);
            dbfField.Value = NormalizeDbfValue(value, field.Type);
        }
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

    private static Geometry CreateNullShapeGeometry(ExportGeometryType geometryType)
    {
        var factory = new GeometryFactory();
        return geometryType switch
        {
            ExportGeometryType.Point or ExportGeometryType.None => factory.CreatePoint(),
            ExportGeometryType.MultiPoint => factory.CreateMultiPoint([]),
            ExportGeometryType.LineString or ExportGeometryType.MultiLineString => factory.CreateMultiLineString([]),
            ExportGeometryType.Polygon or ExportGeometryType.MultiPolygon => factory.CreateMultiPolygon([]),
            _ => throw new InvalidOperationException($"Shapefile format does not support geometry type '{geometryType}'.")
        };
    }

    // NTS writes a point's sequence layout directly. Match the file header, including
    // the optional M slot on Z shapes, so a following record cannot become its M value.
    private sealed class ShapeOrdinateOperation(bool hasZ, bool hasM) : GeometryEditor.CoordinateSequenceOperation
    {
        public override CoordinateSequence Edit(CoordinateSequence sequence, Geometry geometry)
        {
            var result = geometry.Factory.CoordinateSequenceFactory.Create(
                sequence.Count, 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0), hasM ? 1 : 0);
            for (var i = 0; i < sequence.Count; i++)
            {
                var z = sequence.GetZ(i);
                var m = sequence.GetM(i);
                if ((!hasZ && !double.IsNaN(z)) || (!hasM && !double.IsNaN(m)))
                {
                    throw new InvalidOperationException(
                        "Shapefile export cannot preserve varying ordinate dimensions. Export as CSV or GeoPackage instead.");
                }

                result.SetX(i, sequence.GetX(i));
                result.SetY(i, sequence.GetY(i));
                if (hasZ)
                {
                    result.SetZ(i, z);
                }

                if (hasM)
                {
                    result.SetM(i, m);
                }
            }

            return result;
        }
    }

    private static Geometry NormalizeGeometry(Geometry geometry, ExportGeometryType targetType)
    {
        return targetType switch
        {
            // Promote single → multi
            ExportGeometryType.MultiPoint when geometry is Point p => new MultiPoint([p]),
            ExportGeometryType.MultiLineString when geometry is LineString ls => new MultiLineString([ls]),
            ExportGeometryType.MultiPolygon when geometry is Polygon pg => new MultiPolygon([pg]),
            // Preserve multipart line and polygon geometries. Shapefile polyline/polygon
            // records support multiple parts, so taking only the first part corrupts data.
            ExportGeometryType.Point when geometry is MultiPoint =>
                throw new InvalidOperationException("Shapefile export cannot write MultiPoint geometry from a layer declared as Point without corrupting geometry type."),
            _ => geometry
        };
    }

    // Probes the representative geometry for Z/M ordinates so the shapefile header
    // can be stamped with the matching Z/M shape-type variant. Z/M presence is not
    // carried on the layer schema, so it is inferred from the first exported
    // geometry's coordinate: a plain 2D Coordinate reports NaN for both Z and M.
    private static (bool HasZ, bool HasM) DetectZM(Geometry? geometry)
    {
        var coordinate = geometry?.Coordinate;
        if (coordinate is null)
        {
            return (false, false);
        }

        return (!double.IsNaN(coordinate.Z), !double.IsNaN(coordinate.M));
    }

    private static ShapeType MapShapeType(ExportGeometryType geometryType, bool hasZ, bool hasM)
    {
        var baseType = geometryType switch
        {
            ExportGeometryType.Point => ShapeType.Point,
            ExportGeometryType.MultiPoint => ShapeType.MultiPoint,
            ExportGeometryType.LineString or ExportGeometryType.MultiLineString => ShapeType.PolyLine,
            ExportGeometryType.Polygon or ExportGeometryType.MultiPolygon => ShapeType.Polygon,
            ExportGeometryType.None => ShapeType.NullShape,
            _ => throw new InvalidOperationException(
                $"Shapefile format does not support geometry type '{geometryType}'.")
        };

        if (baseType == ShapeType.NullShape)
        {
            return baseType;
        }

        // ESRI shapefile Z shapes (PointZM/PolyLineZM/PolygonZM/MultiPointZM) always
        // carry an optional M array; the M-only shapes (PointM/…) carry M without Z.
        // Promote the 2D base type to the matching variant so the writer serializes
        // the source Z (and/or M) ordinates instead of flattening to 2D (#2744).
        if (hasZ)
        {
            return baseType switch
            {
                ShapeType.Point => ShapeType.PointZM,
                ShapeType.MultiPoint => ShapeType.MultiPointZM,
                ShapeType.PolyLine => ShapeType.PolyLineZM,
                ShapeType.Polygon => ShapeType.PolygonZM,
                _ => baseType
            };
        }

        if (hasM)
        {
            return baseType switch
            {
                ShapeType.Point => ShapeType.PointM,
                ShapeType.MultiPoint => ShapeType.MultiPointM,
                ShapeType.PolyLine => ShapeType.PolyLineM,
                ShapeType.Polygon => ShapeType.PolygonM,
                _ => baseType
            };
        }

        return baseType;
    }

    private static object? NormalizeDbfValue(object? value, ExportFieldType fieldType)
    {
        if (value is null or DBNull)
            return null;

        return fieldType switch
        {
            ExportFieldType.Boolean when value is bool b => b,
            ExportFieldType.Boolean when value is string s &&
                bool.TryParse(s, out var parsedBool) => parsedBool,
            ExportFieldType.DateTime or ExportFieldType.Date => NormalizeDbfDateValue(value),
            ExportFieldType.String
                or ExportFieldType.Time
                or ExportFieldType.Json
                or ExportFieldType.Binary
                or ExportFieldType.Uuid
                or ExportFieldType.Unknown => Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private static DateTime? NormalizeDbfDateValue(object value)
    {
        return value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.DateTime,
            string s when DateTimeOffset.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var dto) => dto.DateTime,
            _ => null
        };
    }
}

/// <summary>
/// Result of a Shapefile write operation.
/// </summary>
internal sealed record ShapefileWriteResult(int WrittenCount, int SkippedNullGeometry, List<string> Warnings);
