// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Streaming CSV reader that supports two geometry encodings:
/// WKT columns and longitude/latitude coordinate pairs.
/// </summary>
internal static class CsvFormatReader
{
    /// <summary>
    /// Maximum size for a single CSV record to prevent memory exhaustion attacks.
    /// Default: 10 MB per record should be sufficient for legitimate geospatial data.
    /// </summary>
    private const int MaxRecordSizeBytes = 10 * 1024 * 1024;
    private static readonly FrozenSet<string> _wktColumnNames = new[]
        {
            "wkt",
            "geomwkt",
            "geometrywkt",
            "thegeom",
            "geom",
            "geometry",
            "shape"
        }
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> _longitudeColumnNames = new[]
        {
            "lon",
            "lng",
            "long",
            "longitude",
            "x"
        }
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> _latitudeColumnNames = new[]
        {
            "lat",
            "latitude",
            "y"
        }
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Stream features from a CSV file.
    /// </summary>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        string[]? headers = null;
        CsvColumnMapping mapping = default;
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var wktReader = new WKTReader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await ReadCsvRecordAsync(reader, cancellationToken);
            if (record == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(record))
            {
                continue;
            }

            var fields = ParseCsvRecord(record);
            if (fields.Count == 0)
            {
                continue;
            }

            if (headers == null)
            {
                headers = NormalizeHeaders(fields);
                mapping = BuildMapping(headers);
                continue;
            }

            var feature = BuildFeature(headers, fields, mapping, geometryFactory, wktReader);
            if (feature != null)
            {
                yield return feature;
            }
        }
    }

    private static Feature? BuildFeature(
        string[] headers,
        IReadOnlyList<string> fields,
        CsvColumnMapping mapping,
        GeometryFactory geometryFactory,
        WKTReader wktReader)
    {
        var attributes = new AttributesTable();

        for (var i = 0; i < headers.Length; i++)
        {
            if (IsGeometrySourceColumn(i, mapping))
            {
                continue;
            }

            var value = GetField(fields, i);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            attributes.Add(headers[i], value);
        }

        var geometry = ParseGeometry(fields, mapping, geometryFactory, wktReader);
        if (geometry == null && attributes.GetNames().Length == 0)
        {
            return null;
        }

        return new Feature(geometry, attributes);
    }

    private static NtsGeometry? ParseGeometry(
        IReadOnlyList<string> fields,
        CsvColumnMapping mapping,
        GeometryFactory geometryFactory,
        WKTReader wktReader)
    {
        if (mapping.WktIndex.HasValue)
        {
            var wktValue = GetField(fields, mapping.WktIndex.Value);
            if (!string.IsNullOrWhiteSpace(wktValue))
            {
                try
                {
                    return wktReader.Read(wktValue.Trim());
                }
                catch
                {
                    // Ignore malformed WKT and try other geometry encodings.
                }
            }
        }

        if (mapping.LongitudeIndex.HasValue && mapping.LatitudeIndex.HasValue)
        {
            var longitudeValue = GetField(fields, mapping.LongitudeIndex.Value);
            var latitudeValue = GetField(fields, mapping.LatitudeIndex.Value);

            if (double.TryParse(longitudeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) &&
                double.TryParse(latitudeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude))
            {
                return geometryFactory.CreatePoint(new Coordinate(longitude, latitude));
            }
        }

        return null;
    }

    private static CsvColumnMapping BuildMapping(string[] headers)
    {
        int? wktIndex = null;
        int? longitudeIndex = null;
        int? latitudeIndex = null;

        for (var i = 0; i < headers.Length; i++)
        {
            var normalized = NormalizeColumnName(headers[i]);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (!wktIndex.HasValue && _wktColumnNames.Contains(normalized))
            {
                wktIndex = i;
                continue;
            }

            if (!longitudeIndex.HasValue && _longitudeColumnNames.Contains(normalized))
            {
                longitudeIndex = i;
                continue;
            }

            if (!latitudeIndex.HasValue && _latitudeColumnNames.Contains(normalized))
            {
                latitudeIndex = i;
            }
        }

        return new CsvColumnMapping(wktIndex, longitudeIndex, latitudeIndex);
    }

    private static bool IsGeometrySourceColumn(int index, CsvColumnMapping mapping)
        => mapping.WktIndex == index || mapping.LongitudeIndex == index || mapping.LatitudeIndex == index;

    private static string GetField(IReadOnlyList<string> fields, int index)
        => index < fields.Count ? fields[index] : string.Empty;

    private static string[] NormalizeHeaders(List<string> rawHeaders)
    {
        var headers = new string[rawHeaders.Count];
        var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rawHeaders.Count; i++)
        {
            var cleaned = rawHeaders[i].Trim().TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                cleaned = $"field_{i + 1}";
            }

            if (usedNames.TryGetValue(cleaned, out var count))
            {
                count++;
                usedNames[cleaned] = count;
                cleaned = $"{cleaned}_{count}";
            }
            else
            {
                usedNames.Add(cleaned, 1);
            }

            headers[i] = cleaned;
        }

        return headers;
    }

    private static List<string> ParseCsvRecord(string record)
    {
        var fields = new List<string>();
        var current = new StringBuilder(record.Length);
        var inQuotes = false;

        for (var i = 0; i < record.Length; i++)
        {
            var ch = record[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    var hasEscapedQuote = i + 1 < record.Length && record[i + 1] == '"';
                    if (hasEscapedQuote)
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            if (ch == '"')
            {
                inQuotes = true;
                continue;
            }

            current.Append(ch);
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static async Task<string?> ReadCsvRecordAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line == null)
        {
            return null;
        }

        var builder = new StringBuilder(line);
        var currentSize = Encoding.UTF8.GetByteCount(line);

        while (!IsCompleteRecord(builder))
        {
            var nextLine = await reader.ReadLineAsync(cancellationToken);
            if (nextLine == null)
            {
                break;
            }

            // Check size before appending to prevent memory exhaustion
            var additionalSize = Encoding.UTF8.GetByteCount(nextLine) + 1; // +1 for newline
            if (currentSize + additionalSize > MaxRecordSizeBytes)
            {
                throw new InvalidOperationException(
                    $"CSV record exceeds maximum size limit of {MaxRecordSizeBytes:N0} bytes. " +
                    "This may indicate malformed CSV with unbalanced quotes or an exceptionally large record.");
            }

            builder.Append('\n');
            builder.Append(nextLine);
            currentSize += additionalSize;
        }

        return builder.ToString();
    }

    private static bool IsCompleteRecord(StringBuilder record)
    {
        var inQuotes = false;
        for (var i = 0; i < record.Length; i++)
        {
            if (record[i] != '"')
            {
                continue;
            }

            var hasEscapedQuote = inQuotes && i + 1 < record.Length && record[i + 1] == '"';
            if (hasEscapedQuote)
            {
                i++;
                continue;
            }

            inQuotes = !inQuotes;
        }

        return !inQuotes;
    }

    private static string NormalizeColumnName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[value.Length];
        var count = 0;
        foreach (var ch in value)
        {
            if (ch is '_' or '-' or ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            buffer[count++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..count]);
    }

    private readonly record struct CsvColumnMapping(
        int? WktIndex,
        int? LongitudeIndex,
        int? LatitudeIndex);
}
