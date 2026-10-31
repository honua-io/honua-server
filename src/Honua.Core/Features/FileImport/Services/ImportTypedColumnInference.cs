// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Infers promotable PostgreSQL column types from source attribute values seen during an import.
/// Unsafe names, reserved import columns, PostgreSQL system columns, nested values, and keys
/// past the column cap stay out of the result so they remain only in the properties document.
/// </summary>
internal sealed partial class ImportTypedColumnInference(bool preserveStringScalars = false)
{
    internal const int MaxColumns = 128;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "geometry",
        "properties",
        "created_at",
        "xmin",
        "xmax",
        "cmin",
        "cmax",
        "ctid",
        "tableoid"
    };

    private readonly Dictionary<string, Kind> _columns = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one attribute value. Null and empty strings do not force a type on their own.
    /// </summary>
    public void Observe(string name, object? value)
    {
        if (!IsPromotableName(name))
        {
            return;
        }

        if (!_columns.TryGetValue(name, out var current) && _columns.Count >= MaxColumns)
        {
            return;
        }

        var observed = Classify(value, preserveStringScalars);
        _columns[name] = current == Kind.None ? observed : Merge(current, observed);
    }

    /// <summary>
    /// Returns the columns that can be promoted, in first-seen order.
    /// </summary>
    public IReadOnlyList<ImportTypedColumn> Resolve()
    {
        var columns = new List<ImportTypedColumn>(_columns.Count);
        foreach (var (name, kind) in _columns)
        {
            if (PostgresType(kind) is { } postgresType)
            {
                columns.Add(new ImportTypedColumn { Name = name, PostgresType = postgresType });
            }
        }

        return columns;
    }

    private static bool IsPromotableName(string name)
        => name.Length is > 0 and <= 63
            && IdentifierPattern().IsMatch(name)
            && !ReservedNames.Contains(name);

    private static Kind Classify(object? value, bool preserveStringScalars) => value switch
    {
        null or DBNull => Kind.Null,
        string text => ClassifyString(text, preserveStringScalars),
        bool => Kind.Boolean,
        JsonElement element => ClassifyJsonElement(element, preserveStringScalars),
        byte or sbyte or short or ushort or int or uint or long => Kind.Bigint,
        ulong unsigned => ClassifyUnsigned(unsigned),
        float or double => Kind.Double,
        decimal number => ClassifyDecimal(number),
        DateOnly => Kind.Date,
        DateTime or DateTimeOffset => Kind.Timestamp,
        Guid => Kind.Text,
        byte[] or TimeOnly => Kind.Reject,
        IDictionary or IEnumerable => Kind.Reject,
        _ => Kind.Reject
    };

    private static Kind ClassifyUnsigned(ulong value)
    {
        if (value <= long.MaxValue)
        {
            return Kind.Bigint;
        }

        var widened = (double)value;
        return (ulong)widened == value ? Kind.Double : Kind.Reject;
    }

    private static Kind ClassifyDecimal(decimal value)
    {
        if (value == decimal.Truncate(value) && value >= long.MinValue && value <= long.MaxValue)
        {
            return Kind.Bigint;
        }

        return Kind.Double;
    }

    private static Kind ClassifyJsonElement(JsonElement element, bool preserveStringScalars) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => Kind.Null,
        JsonValueKind.True or JsonValueKind.False => Kind.Boolean,
        JsonValueKind.String => ClassifyString(element.GetString() ?? string.Empty, preserveStringScalars),
        JsonValueKind.Number when element.TryGetInt64(out _) => Kind.Bigint,
        JsonValueKind.Number when element.TryGetDouble(out var number) && double.IsFinite(number) => Kind.Double,
        _ => Kind.Reject
    };

    private static Kind ClassifyString(string value, bool preserveStringScalars)
    {
        // JSON has explicit scalar types. Preserve numeric and boolean-looking strings,
        // such as source identifiers with leading zeroes, while still recognizing dates.
        if (preserveStringScalars)
        {
            if (DatePattern().IsMatch(value)
                && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return Kind.Date;
            }

            if (TimestampPrefixPattern().IsMatch(value)
                && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            {
                return Kind.Timestamp;
            }

            return Kind.Text;
        }

        if (value.Length == 0)
        {
            return Kind.Null;
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return Kind.Boolean;
        }

        if (IntegerPattern().IsMatch(value))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return Kind.Bigint;
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var widened)
                && double.IsFinite(widened))
            {
                return Kind.Double;
            }
        }

        if (FloatPattern().IsMatch(value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number))
        {
            return Kind.Double;
        }

        if (DatePattern().IsMatch(value)
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return Kind.Date;
        }

        if (TimestampPrefixPattern().IsMatch(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            return Kind.Timestamp;
        }

        return Kind.Text;
    }

    private static Kind Merge(Kind current, Kind next)
    {
        if (current is Kind.Reject || next is Kind.Reject)
        {
            return Kind.Reject;
        }

        if (current is Kind.None or Kind.Null)
        {
            return next;
        }

        if (next is Kind.Null)
        {
            return current;
        }

        if (current == next)
        {
            return current;
        }

        if ((current is Kind.Bigint && next is Kind.Double) || (current is Kind.Double && next is Kind.Bigint))
        {
            return Kind.Double;
        }

        return Kind.Text;
    }

    private static string? PostgresType(Kind kind) => kind switch
    {
        Kind.Null or Kind.Text => "text",
        Kind.Bigint => "bigint",
        Kind.Double => "double precision",
        Kind.Boolean => "boolean",
        Kind.Date => "date",
        Kind.Timestamp => "timestamp with time zone",
        _ => null
    };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^-?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^-?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FloatPattern();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{2}:[0-9]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPrefixPattern();

    private enum Kind
    {
        None = 0,
        Null,
        Bigint,
        Double,
        Boolean,
        Text,
        Date,
        Timestamp,
        Reject
    }
}
