// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

internal static class CsvFormatReader
{
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

    internal static IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        CancellationToken cancellationToken)
        => ReadStreamingAsync(stream, delimiterOverride: null, diagnostics: null, cancellationToken);

    internal static IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        char? delimiterOverride,
        CancellationToken cancellationToken)
        => ReadStreamingAsync(stream, delimiterOverride, diagnostics: null, cancellationToken);

    internal static IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        char? delimiterOverride,
        CsvGeometryDiagnostics? diagnostics,
        CancellationToken cancellationToken)
        => ReadStreamingAsync(stream, delimiterOverride, diagnostics, options: null, cancellationToken);

    internal static async Task<int?> TryDetectGeometrySridAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        List<string> sampleRecords;
        try
        {
            sampleRecords = await ReadSampleRecordsAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return null;
        }

        if (sampleRecords.Count < 2)
        {
            return null;
        }

        var delimiter = DetectDelimiter(sampleRecords);
        var headers = NormalizeHeaders(ParseCsvRecord(sampleRecords[0], delimiter));
        var mapping = BuildMapping(headers, options: null);
        if (!mapping.WktIndex.HasValue)
        {
            return null;
        }

        var wktReader = new WKTReader();
        var wkbReader = GeometryValueParser.CreateWkbReader();
        for (var i = 1; i < sampleRecords.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = ParseCsvRecord(sampleRecords[i], delimiter);
            var value = GetField(fields, mapping.WktIndex.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var geometry = GeometryValueParser.TryParse(value, wktReader, wkbReader);
            if (geometry is { SRID: > 0 })
            {
                return geometry.SRID;
            }
        }

        return null;
    }

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
            var quotedFields = new HashSet<int>();
            var fields = ParseCsvRecord(record, delimiter, quotedFields);
            if (fields.Count == 0)
            {
                return null;
            }

            if (headers == null)
            {
                headers = NormalizeHeaders(fields);
                if (diagnostics is not null)
                {
                    diagnostics.Headers = headers;
                }
                // Spreadsheets in most European locales write ';'-separated files with decimal-comma numbers. Tab and
                // pipe files are as often written where ',' groups thousands, so only ';' files read a decimal comma.
                mapping = BuildMapping(headers, options) with { DecimalComma = delimiter == ';' };
                return null;
            }

            dataRowNumber++;
            return BuildFeature(headers, fields, quotedFields, mapping, geometryFactory, wktReader, wkbReader, diagnostics);
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
            var record = await ReadCsvRecordAsync(reader, delimiter, cancellationToken);
            if (record == null)
            {
                break;
            }

            if (string.IsNullOrEmpty(record))
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
        HashSet<int> quotedFields,
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
            if (value.Length == 0 && !quotedFields.Contains(i))
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
        // Coordinate columns that failed to parse are restored the same way, including
        // heuristic lat/lon pairs. Blank cells stay a null geometry with no warning.
        var coordinatesUnparseable = geometry == null && HasUnparseableCoordinates(fields, mapping);
        if (geometry == null && (unparseableGeometryValue != null || coordinatesUnparseable))
        {
            diagnostics?.RecordUnparseableGeometry();
            if (unparseableGeometryValue != null && mapping.WktIndex.HasValue)
            {
                attributes.Add(headers[mapping.WktIndex.Value], unparseableGeometryValue);
            }

            if (coordinatesUnparseable)
            {
                RestoreUnparseableCoordinate(attributes, headers, fields, mapping.LongitudeIndex);
                RestoreUnparseableCoordinate(attributes, headers, fields, mapping.LatitudeIndex);
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

            if (ParseCoordinate(longitudeValue, mapping) is { } longitude &&
                ParseCoordinate(latitudeValue, mapping) is { } latitude)
            {
                // A parseable coordinate pair supersedes an unparseable WKT column.
                unparseableGeometryValue = null;
                return geometryFactory.CreatePoint(new Coordinate(longitude, latitude));
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

        // A point needs both axes. One coordinate-like header (an "x" score, a "long"
        // description, a lat column with no longitude) is an ordinary attribute.
        if (longitudeIndex.HasValue != latitudeIndex.HasValue)
        {
            longitudeIndex = null;
            latitudeIndex = null;
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

    private static bool HasUnparseableCoordinates(IReadOnlyList<string> fields, CsvColumnMapping mapping)
    {
        if (mapping.LongitudeIndex is not { } longitudeIndex || mapping.LatitudeIndex is not { } latitudeIndex)
        {
            return false;
        }

        var longitudeValue = GetField(fields, longitudeIndex);
        var latitudeValue = GetField(fields, latitudeIndex);
        if (string.IsNullOrWhiteSpace(longitudeValue) && string.IsNullOrWhiteSpace(latitudeValue))
        {
            return false;
        }

        return ParseCoordinate(longitudeValue, mapping) is null || ParseCoordinate(latitudeValue, mapping) is null;
    }

    // Invariant numbers always; a decimal comma ("11,575490": one comma, digits on both sides, no point) only in
    // ';'-separated files.
    private static double? ParseCoordinate(string value, CsvColumnMapping mapping)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant))
            return invariant;
        var trimmed = value.Trim();
        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (!mapping.DecimalComma || comma <= 0 || comma == trimmed.Length - 1
            || trimmed.IndexOf(',', comma + 1) >= 0 || trimmed.Contains('.', StringComparison.Ordinal)
            || !char.IsAsciiDigit(trimmed[comma - 1]) || !char.IsAsciiDigit(trimmed[comma + 1]))
            return null;
        return double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalComma)
            ? decimalComma
            : null;
    }

    private static void RestoreUnparseableCoordinate(
        AttributesTable attributes,
        string[] headers,
        IReadOnlyList<string> fields,
        int? index)
    {
        if (index is not { } column || attributes.Exists(headers[column]))
        {
            return;
        }

        attributes.Add(headers[column], GetField(fields, column));
    }

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
            var record = await ReadCsvRecordAsync(reader, delimiter: null, cancellationToken);
            if (record == null)
            {
                break;
            }

            // Leading padding is not a header. After the header, whitespace can
            // be a real value in an attribute-only row and must remain intact.
            if (string.IsNullOrEmpty(record) ||
                (sampleRecords.Count == 0 && string.IsNullOrWhiteSpace(record)))
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

    private static List<string> ParseCsvRecord(string record, char delimiter, HashSet<int>? quotedFields = null)
    {
        var fields = new List<string>();
        var current = new StringBuilder(record.Length);
        var inQuotes = false;
        var atFieldStart = true;

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

                atFieldStart = false;
                continue;
            }

            if (ch == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
                atFieldStart = true;
                continue;
            }

            // A quote opens a field only when it is the first character of that field.
            // A stray inch-mark in 12" is literal text, not a multi-line quoted field.
            if (ch == '"' && atFieldStart)
            {
                quotedFields?.Add(fields.Count);
                inQuotes = true;
                atFieldStart = false;
                continue;
            }

            current.Append(ch);
            atFieldStart = false;
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static async Task<string?> ReadCsvRecordAsync(
        StreamReader reader,
        char? delimiter,
        CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line == null)
        {
            return null;
        }

        // Quote state advances once per physical line. Rescanning the accumulated
        // record made one multi-line field quadratic in its length.
        var cursor = CsvQuoteCursor.Start.Advance(line, delimiter);
        if (!cursor.InQuotes)
        {
            return line;
        }

        var builder = new StringBuilder(line);
        var currentSize = Encoding.UTF8.GetByteCount(line);

        while (cursor.InQuotes)
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
            cursor = cursor.Advance(nextLine, delimiter);
        }

        return builder.ToString();
    }

    private readonly struct CsvQuoteCursor
    {
        public bool InQuotes { get; private init; }

        public bool AtFieldStart { get; private init; }

        public static CsvQuoteCursor Start => new() { AtFieldStart = true };

        public CsvQuoteCursor Advance(ReadOnlySpan<char> text, char? delimiter)
        {
            var inQuotes = InQuotes;
            var atFieldStart = AtFieldStart;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        var hasEscapedQuote = i + 1 < text.Length && text[i + 1] == '"';
                        if (hasEscapedQuote)
                        {
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }

                    atFieldStart = false;
                    continue;
                }

                if (IsFieldDelimiter(ch, delimiter))
                {
                    atFieldStart = true;
                    continue;
                }

                if (ch == '"' && atFieldStart)
                {
                    inQuotes = true;
                    atFieldStart = false;
                    continue;
                }

                atFieldStart = false;
            }

            return new CsvQuoteCursor { InQuotes = inQuotes, AtFieldStart = atFieldStart };
        }
    }

    private static bool IsFieldDelimiter(char ch, char? delimiter)
    {
        if (delimiter.HasValue)
        {
            return ch == delimiter.Value;
        }

        return ch is ',' or '\t' or ';' or '|';
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
        bool ExplicitCoordinates = false)
    {
        // Set for ';'-separated files, where a comma inside a coordinate can only be a decimal comma.
        public bool DecimalComma { get; init; }
    }
}

internal readonly record struct CsvGeocodeFailure(int RowNumber, string Address);

internal sealed class CsvGeometryDiagnostics
{
    private const int MaxRecordedGeocodeFailures = 100;

    private List<CsvGeocodeFailure>? _geocodeFailures;

    public IReadOnlyList<string> Headers { get; internal set; } = [];

    public int UnparseableGeometryRows { get; private set; }

    public void RecordUnparseableGeometry() => UnparseableGeometryRows++;

    public int GeocodeFailureCount { get; private set; }

    public IReadOnlyList<CsvGeocodeFailure> GeocodeFailures =>
        (IReadOnlyList<CsvGeocodeFailure>?)_geocodeFailures ?? [];

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
