// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Shared.Services;

public static class CsvFieldFormatter
{
    public static string Escape(string? value)
    {
        if (value is null)
            return string.Empty;
        if (value.Length == 0)
            return "\"\"";
        var field = IsPotentialSpreadsheetFormula(value) ? string.Concat("'", value) : value;
        if (field.AsSpan().IndexOfAny(',', '"', '\n') >= 0 || field.Contains('\r'))
            return string.Concat("\"", field.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
        return field;
    }

    public static bool IsPotentialSpreadsheetFormula(ReadOnlySpan<char> field)
    {
        var start = 0;
        while (start < field.Length && char.IsWhiteSpace(field[start]))
        {
            start++;
        }

        if (start >= field.Length)
        {
            return false;
        }

        return field[start] switch
        {
            '=' or '@' => true,
            // Signed plain numbers stay numbers.
            '+' or '-' => !IsSignedNumber(field[start..]),
            _ => false,
        };
    }

    // double.TryParse also accepts words such as Infinity and NaN, which a spreadsheet reads as names.
    private static bool IsSignedNumber(ReadOnlySpan<char> field) =>
        field.Length > 1
        && (char.IsAsciiDigit(field[1]) || field[1] == '.')
        && double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
