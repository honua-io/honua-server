// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Shared.Services;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Features.Services;

// Builds OGC items CSV rows into one reused buffer. Fields are escaped while they are
// appended (spreadsheet formulas neutralized, RFC 4180 quoting), numbers are formatted
// without an intermediate string and geometry cells are written through one reused
// Utf8JsonWriter, so a row costs no per-field strings or lists.
internal sealed class OgcCsvRowBuilder : IDisposable
{
    private static readonly SearchValues<char> QuotedFieldChars = SearchValues.Create(",\"\n\r");

    private readonly string[] _fieldNames;
    private readonly string? _identifierFieldName;
    private readonly StringBuilder _row = new();
    private readonly ArrayBufferWriter<byte> _geometryJson = new(256);
    private readonly Utf8JsonWriter _geometryWriter;
    private char[] _chars = ArrayPool<char>.Shared.Rent(256);

    public OgcCsvRowBuilder(string[] fieldNames, string? identifierFieldName)
    {
        _fieldNames = fieldNames;
        _identifierFieldName = identifierFieldName;
        _geometryWriter = new Utf8JsonWriter(_geometryJson);
    }

    public string BuildHeader()
    {
        var usedNames = new HashSet<string>(_fieldNames, StringComparer.OrdinalIgnoreCase);
        var idHeader = ColumnNameReservation.Reserve("id", usedNames);
        var geometryHeader = ColumnNameReservation.Reserve("geometry", usedNames);
        _row.Clear();
        AppendField(idHeader);
        foreach (var fieldName in _fieldNames)
        {
            _row.Append(',');
            AppendField(fieldName);
        }

        _row.Append(',');
        AppendField(geometryHeader);
        return _row.ToString();
    }

    // The row for feature, valid until the next call.
    public StringBuilder BuildRow(GeoJsonFeature feature)
    {
        _row.Clear();
        AppendValue(feature.Id);
        foreach (var fieldName in _fieldNames)
        {
            _row.Append(',');
            if (feature.Properties?.TryGetValue(fieldName, out var value) == true)
            {
                AppendValue(value);
            }
            else if (fieldName.Equals(_identifierFieldName, StringComparison.OrdinalIgnoreCase))
            {
                AppendValue(feature.Id);
            }
        }

        _row.Append(',');
        AppendGeometry(feature.Geometry);
        return _row;
    }

    public void Dispose()
    {
        _geometryWriter.Dispose();
        ArrayPool<char>.Shared.Return(_chars);
        _chars = [];
    }

    // Matches value?.ToString(): the numeric types format with the current culture, as
    // ToString() does, straight into the char buffer.
    private void AppendValue(object? value)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                AppendField(text);
                return;
            case long or int or short or double or float or decimal:
                AppendFormatted((ISpanFormattable)value);
                return;
            default:
                var formatted = value.ToString();
                if (formatted is not null)
                {
                    AppendField(formatted);
                }

                return;
        }
    }

    private void AppendFormatted(ISpanFormattable value)
    {
        int written;
        while (!value.TryFormat(_chars, out written, default, provider: null))
        {
            GrowChars(_chars.Length * 2);
        }

        AppendField(_chars.AsSpan(0, written));
    }

    private void AppendGeometry(SimpleGeoJsonGeometry? geometry)
    {
        if (geometry == null || string.IsNullOrWhiteSpace(geometry.Type))
        {
            return;
        }

        _geometryJson.ResetWrittenCount();
        _geometryWriter.Reset(_geometryJson);
        if (!TryWriteGeometryJson(geometry))
        {
            AppendField(geometry.Type);
            return;
        }

        var json = _geometryJson.WrittenSpan;
        GrowChars(Encoding.UTF8.GetMaxCharCount(json.Length));
        var length = Encoding.UTF8.GetChars(json, _chars);
        AppendField(_chars.AsSpan(0, length));
    }

    // Written through Utf8JsonWriter rather than a reflection-based JsonSerializer overload:
    // the published image disables reflection-based serialization. Coordinates that do not
    // parse fall back to the bare type name.
    private bool TryWriteGeometryJson(SimpleGeoJsonGeometry geometry)
    {
        if (string.IsNullOrWhiteSpace(geometry.CoordinatesJson))
        {
            WriteGeometryObject(geometry.Type, coordinates: null);
            return true;
        }

        try
        {
            using var coordinatesDocument = JsonDocument.Parse(geometry.CoordinatesJson);
            WriteGeometryObject(geometry.Type, coordinatesDocument.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void WriteGeometryObject(string type, JsonElement? coordinates)
    {
        _geometryWriter.WriteStartObject();
        _geometryWriter.WriteString("type", type);
        if (coordinates is { } element)
        {
            _geometryWriter.WritePropertyName("coordinates");
            element.WriteTo(_geometryWriter);
        }

        _geometryWriter.WriteEndObject();
        _geometryWriter.Flush();
    }

    // Same output as CsvFieldFormatter.Escape over the formula-neutralized field.
    private void AppendField(ReadOnlySpan<char> field)
    {
        if (field.IsEmpty)
        {
            _row.Append("\"\"");
            return;
        }

        var quoted = field.IndexOfAny(QuotedFieldChars) >= 0;
        if (quoted)
        {
            _row.Append('"');
        }

        if (CsvFieldFormatter.IsPotentialSpreadsheetFormula(field))
        {
            _row.Append('\'');
        }

        if (quoted)
        {
            AppendWithDoubledQuotes(field);
            _row.Append('"');
        }
        else
        {
            _row.Append(field);
        }
    }

    private void AppendWithDoubledQuotes(ReadOnlySpan<char> field)
    {
        int quote;
        while ((quote = field.IndexOf('"')) >= 0)
        {
            _row.Append(field[..(quote + 1)]).Append('"');
            field = field[(quote + 1)..];
        }

        _row.Append(field);
    }

    private void GrowChars(int minimumLength)
    {
        if (_chars.Length >= minimumLength)
        {
            return;
        }

        ArrayPool<char>.Shared.Return(_chars);
        _chars = ArrayPool<char>.Shared.Rent(minimumLength);
    }
}
