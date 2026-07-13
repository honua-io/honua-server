// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services;

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
    private const int DelimiterProbeRecordCount = 5;
    private static readonly char[] _candidateDelimiters = [',', '\t', ';', '|'];
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
    internal static IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        CancellationToken cancellationToken)
        => ReadStreamingAsync(stream, delimiterOverride: null, diagnostics: null, cancellationToken);

    /// <summary>
    /// Stream features from a CSV file with an optional explicit delimiter override.
    /// </summary>
    internal static IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        char? delimiterOverride,
        CancellationToken cancellationToken)
        => ReadStreamingAsync(stream, delimiterOverride, diagnostics: null, cancellationToken);

    /// <summary>
    /// Stream features from a CSV file with an optional explicit delimiter override and an
    /// optional <see cref="CsvGeometryDiagnostics"/> sink. When a row has a mapped geometry
    /// column whose value is present but cannot be parsed as WKT/EWKT/WKB, the raw value is
    /// preserved as an attribute (so the data is never discarded) and the occurrence is
    /// recorded on <paramref name="diagnostics"/> so the caller can surface a warning rather
    /// than silently importing a null geometry.
    /// </summary>
    internal static IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        char? delimiterOverride,
        CsvGeometryDiagnostics? diagnostics,
        CancellationToken cancellationToken)
        => ReadStreamingAsync(stream, delimiterOverride, diagnostics, options: null, cancellationToken);

    /// <summary>
    /// Stream features from a CSV file with optional explicit <see cref="CsvImportOptions"/>.
    /// When the options carry explicit longitude/latitude column names they replace the
    /// header auto-detection heuristics; when they carry an address column, each row's
    /// address value is resolved into a point through the caller-supplied
    /// <see cref="CsvImportOptions.AddressGeocoder"/> (failed rows keep their attributes
    /// and are recorded on <paramref name="diagnostics"/>).
    /// </summary>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        char? delimiterOverride,
        CsvGeometryDiagnostics? diagnostics,
        CsvImportOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        string[]? headers = null;
        CsvColumnMapping mapping = default;
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var wktReader = new WKTReader();
        var wkbReader = GeometryValueParser.CreateWkbReader();
        var sampleRecords = await ReadSampleRecordsAsync(reader, cancellationToken);
        var delimiter = delimiterOverride ?? DetectDelimiter(sampleRecords);
        var dataRowNumber = 0;
        var geocodedRows = 0;

        Feature? ProcessRecord(string record)
        {
            var fields = ParseCsvRecord(record, delimiter);
            if (fields.Count == 0)
            {
                return null;
            }

            if (headers == null)
            {
                headers = NormalizeHeaders(fields);
                mapping = BuildMapping(headers, options);
                return null;
            }

            dataRowNumber++;
            return BuildFeature(headers, fields, mapping, geometryFactory, wktReader, wkbReader, diagnostics);
        }

        async Task ApplyAddressGeocodeAsync(Feature feature)
        {
            if (mapping.AddressIndex is not { } addressIndex || options?.AddressGeocoder is not { } geocoder)
            {
                return;
            }

            if (feature.Geometry != null)
            {
                return;
            }

            var headerName = headers![addressIndex];
            var addressValue = feature.Attributes.Exists(headerName) ? feature.Attributes[headerName] as string : null;
            if (string.IsNullOrWhiteSpace(addressValue))
            {
                diagnostics?.RecordGeocodeFailure(dataRowNumber, string.Empty);
                return;
            }

            geocodedRows++;
            if (options.MaxGeocodedRows is { } cap && geocodedRows > cap)
            {
                throw new CsvImportOptionsException(
                    $"CSV address geocoding is capped at {cap} rows per import. "
                    + "Geocode larger datasets in batches first, then import the coordinates directly.");
            }

            var resolved = await geocoder(addressValue.Trim(), cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                diagnostics?.RecordGeocodeFailure(dataRowNumber, addressValue.Trim());
                return;
            }

            feature.Geometry = geometryFactory.CreatePoint(new Coordinate(resolved.Longitude, resolved.Latitude));
        }

        foreach (var sampleRecord in sampleRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = ProcessRecord(sampleRecord);
            if (feature != null)
            {
                await ApplyAddressGeocodeAsync(feature).ConfigureAwait(false);
                yield return feature;
            }
        }

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

            var feature = ProcessRecord(record);
            if (feature != null)
            {
                await ApplyAddressGeocodeAsync(feature).ConfigureAwait(false);
                yield return feature;
            }
        }
    }

    private static Feature? BuildFeature(
        string[] headers,
        IReadOnlyList<string> fields,
        CsvColumnMapping mapping,
        GeometryFactory geometryFactory,
        WKTReader wktReader,
        WKBReader wkbReader,
        CsvGeometryDiagnostics? diagnostics)
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

        var geometry = ParseGeometry(
            fields, mapping, geometryFactory, wktReader, wkbReader, out var unparseableGeometryValue);

        // A geometry column was mapped and carried a value, but none of the supported
        // encodings (WKT/EWKT/WKB hex) could parse it. Rather than silently dropping the
        // geometry to NULL and reporting success (data loss), preserve the raw value as an
        // attribute so it is never discarded and record the occurrence so the import surfaces
        // a warning. Mirrors GeoParquet's skip/warn behaviour for null geometry.
        if (geometry == null && unparseableGeometryValue != null)
        {
            diagnostics?.RecordUnparseableGeometry();
            if (mapping.WktIndex.HasValue)
            {
                attributes.Add(headers[mapping.WktIndex.Value], unparseableGeometryValue);
            }
        }

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
        WKTReader wktReader,
        WKBReader wkbReader,
        out string? unparseableGeometryValue)
    {
        unparseableGeometryValue = null;

        if (mapping.WktIndex.HasValue)
        {
            var wktValue = GetField(fields, mapping.WktIndex.Value);
            if (!string.IsNullOrWhiteSpace(wktValue))
            {
                // Handles plain WKT, EWKT (SRID=<n>; prefix — the standard PostGIS export
                // form) and WKB/EWKB hex. This is what previously fell into a bare catch{}
                // and silently produced a NULL geometry.
                var geometry = GeometryValueParser.TryParse(wktValue, wktReader, wkbReader);
                if (geometry != null)
                {
                    return geometry;
                }

                // Remember the raw value so the caller can preserve it and warn; fall through
                // to the lon/lat encoding in case the file also carries coordinate columns.
                unparseableGeometryValue = wktValue.Trim();
            }
        }

        if (mapping.LongitudeIndex.HasValue && mapping.LatitudeIndex.HasValue)
        {
            var longitudeValue = GetField(fields, mapping.LongitudeIndex.Value);
            var latitudeValue = GetField(fields, mapping.LatitudeIndex.Value);

            if (double.TryParse(longitudeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) &&
                double.TryParse(latitudeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude))
            {
                // A parseable coordinate pair supersedes an unparseable WKT column.
                unparseableGeometryValue = null;
                return geometryFactory.CreatePoint(new Coordinate(longitude, latitude));
            }

            // Caller-specified coordinate columns that carried values but failed to parse
            // are surfaced (heuristic mappings keep the historical silent-null behavior so
            // attribute-only CSVs do not suddenly emit warnings).
            if (mapping.ExplicitCoordinates
                && (!string.IsNullOrWhiteSpace(longitudeValue) || !string.IsNullOrWhiteSpace(latitudeValue)))
            {
                unparseableGeometryValue = $"{longitudeValue},{latitudeValue}".Trim();
            }
        }

        return null;
    }

    private static CsvColumnMapping BuildMapping(string[] headers, CsvImportOptions? options)
    {
        if (options is not null
            && (options.LongitudeColumn is not null || options.LatitudeColumn is not null || options.AddressColumn is not null))
        {
            return BuildExplicitMapping(headers, options);
        }

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

    private static CsvColumnMapping BuildExplicitMapping(string[] headers, CsvImportOptions options)
    {
        if (options.AddressColumn is not null
            && (options.LongitudeColumn is not null || options.LatitudeColumn is not null))
        {
            throw new CsvImportOptionsException(
                "An address column and explicit longitude/latitude columns are mutually exclusive; specify one geometry source.");
        }

        if (options.AddressColumn is { } addressColumn)
        {
            var addressIndex = FindColumn(headers, addressColumn)
                ?? throw new CsvImportOptionsException(
                    $"CSV header does not contain the requested address column '{addressColumn}'. {DescribeHeaders(headers)}");
            return new CsvColumnMapping(null, null, null, AddressIndex: addressIndex);
        }

        if (options.LongitudeColumn is null || options.LatitudeColumn is null)
        {
            throw new CsvImportOptionsException(
                "Explicit coordinate columns must be specified as a pair: both the longitude and the latitude column name are required.");
        }

        var longitudeIndex = FindColumn(headers, options.LongitudeColumn)
            ?? throw new CsvImportOptionsException(
                $"CSV header does not contain the requested longitude column '{options.LongitudeColumn}'. {DescribeHeaders(headers)}");
        var latitudeIndex = FindColumn(headers, options.LatitudeColumn)
            ?? throw new CsvImportOptionsException(
                $"CSV header does not contain the requested latitude column '{options.LatitudeColumn}'. {DescribeHeaders(headers)}");
        if (longitudeIndex == latitudeIndex)
        {
            throw new CsvImportOptionsException(
                "The longitude and latitude column names resolve to the same CSV column.");
        }

        return new CsvColumnMapping(null, longitudeIndex, latitudeIndex, ExplicitCoordinates: true);
    }

    private static int? FindColumn(string[] headers, string requested)
    {
        var trimmed = requested.Trim();
        for (var i = 0; i < headers.Length; i++)
        {
            if (string.Equals(headers[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        // Fall back to the tolerant normalization used by the heuristics so
        // "Longitude " or "longitude_deg" style requests match their headers.
        var normalizedRequest = NormalizeColumnName(trimmed);
        if (normalizedRequest.Length == 0)
        {
            return null;
        }

        for (var i = 0; i < headers.Length; i++)
        {
            if (string.Equals(NormalizeColumnName(headers[i]), normalizedRequest, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return null;
    }

    private static string DescribeHeaders(string[] headers)
    {
        const int maxListed = 25;
        var listed = headers.Length <= maxListed ? headers : headers[..maxListed];
        var suffix = headers.Length > maxListed ? ", …" : string.Empty;
        return $"Available columns: {string.Join(", ", listed)}{suffix}.";
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

    private static async Task<List<string>> ReadSampleRecordsAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var sampleRecords = new List<string>(DelimiterProbeRecordCount);

        while (sampleRecords.Count < DelimiterProbeRecordCount)
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

            sampleRecords.Add(record);
        }

        return sampleRecords;
    }

    private static char DetectDelimiter(List<string> sampleRecords)
    {
        if (sampleRecords.Count == 0)
        {
            return ',';
        }

        var bestDelimiter = ',';
        var bestScore = int.MinValue;
        var header = sampleRecords[0];

        foreach (var candidate in _candidateDelimiters)
        {
            var fieldCounts = new int[sampleRecords.Count];
            for (var i = 0; i < sampleRecords.Count; i++)
            {
                fieldCounts[i] = ParseCsvRecord(sampleRecords[i], candidate).Count;
            }

            var headerFieldCount = fieldCounts[0];
            var matchingRows = 0;
            for (var i = 0; i < fieldCounts.Length; i++)
            {
                if (fieldCounts[i] == headerFieldCount)
                {
                    matchingRows++;
                }
            }

            var delimiterOccurrences = CountOccurrences(header, candidate);
            var score = (headerFieldCount > 1 ? 1_000_000 : 0)
                + (matchingRows * 10_000)
                + (headerFieldCount * 100)
                + delimiterOccurrences;

            if (score > bestScore)
            {
                bestDelimiter = candidate;
                bestScore = score;
            }
        }

        return bestDelimiter;
    }

    private static int CountOccurrences(string value, char candidate) => value.Count(ch => ch == candidate);

    private static List<string> ParseCsvRecord(string record, char delimiter)
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

            if (ch == delimiter)
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
                // EOF reached with an open quote — the CSV is malformed and any
                // downstream parse would yield a silently truncated record.
                throw new InvalidDataException(
                    "CSV file ended while still inside a quoted field. "
                    + "The last record contains an unbalanced double-quote.");
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

        // Only stack-allocate for small inputs. A pathologically long header cell
        // is untrusted and could otherwise overflow the stack, so fall back to the
        // shared array pool for large values.
        const int StackAllocThreshold = 256;
        char[]? rented = null;
        Span<char> buffer = value.Length <= StackAllocThreshold
            ? stackalloc char[StackAllocThreshold]
            : (rented = ArrayPool<char>.Shared.Rent(value.Length));

        try
        {
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
        finally
        {
            if (rented != null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private readonly record struct CsvColumnMapping(
        int? WktIndex,
        int? LongitudeIndex,
        int? LatitudeIndex,
        int? AddressIndex = null,
        bool ExplicitCoordinates = false);
}

/// <summary>
/// A CSV row whose configured address column could not be resolved into a
/// coordinate. The row is imported without geometry; the failure is surfaced as
/// a per-row validation issue on the import result.
/// </summary>
/// <param name="RowNumber">1-based data-row ordinal (header excluded).</param>
/// <param name="Address">The trimmed address text that failed to resolve; empty when the row had no address value.</param>
internal readonly record struct CsvGeocodeFailure(int RowNumber, string Address);

/// <summary>
/// Collects diagnostics emitted while streaming a CSV so the import pipeline can surface
/// warnings instead of silently importing rows with dropped geometry. A single instance is
/// passed to <see cref="CsvFormatReader"/> and read after enumeration completes.
/// </summary>
internal sealed class CsvGeometryDiagnostics
{
    /// <summary>
    /// Upper bound on individually recorded geocode failures so a pathological
    /// input cannot grow the diagnostics without bound; the count keeps advancing.
    /// </summary>
    private const int MaxRecordedGeocodeFailures = 100;

    private List<CsvGeocodeFailure>? _geocodeFailures;

    /// <summary>
    /// Number of rows whose mapped geometry column held a value that could not be parsed as
    /// WKT, EWKT, or WKB/EWKB hex. Such rows are imported without geometry but with the raw
    /// value preserved as an attribute.
    /// </summary>
    public int UnparseableGeometryRows { get; private set; }

    /// <summary>Records that a row had an unparseable geometry value.</summary>
    public void RecordUnparseableGeometry() => UnparseableGeometryRows++;

    /// <summary>Total number of rows whose address value could not be geocoded.</summary>
    public int GeocodeFailureCount { get; private set; }

    /// <summary>
    /// Individually recorded geocode failures, capped at 100 entries
    /// (<see cref="GeocodeFailureCount"/> is not capped).
    /// </summary>
    public IReadOnlyList<CsvGeocodeFailure> GeocodeFailures =>
        (IReadOnlyList<CsvGeocodeFailure>?)_geocodeFailures ?? [];

    /// <summary>Records that a row's address value could not be geocoded.</summary>
    /// <param name="rowNumber">1-based data-row ordinal (header excluded).</param>
    /// <param name="address">The trimmed address text; empty when the row had no address value.</param>
    public void RecordGeocodeFailure(int rowNumber, string address)
    {
        GeocodeFailureCount++;
        _geocodeFailures ??= [];
        if (_geocodeFailures.Count < MaxRecordedGeocodeFailures)
        {
            _geocodeFailures.Add(new CsvGeocodeFailure(rowNumber, address));
        }
    }
}
