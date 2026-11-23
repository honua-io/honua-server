// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Core.Queries.Filters;

public static class SqlIdentifier
{
    // PostgreSQL truncates identifiers beyond NAMEDATALEN - 1 bytes, which would let two
    // distinct column names collapse onto one.
    private const int MaxIdentifierUtf8Bytes = 63;

    public static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // A column-style name: a letter of any script or underscore, then letters, combining
    // marks, decimal digits or underscores, within PostgreSQL's 63-byte limit. Quotes,
    // whitespace, punctuation and control characters are rejected, so a passing name is
    // safe to emit through QuoteIdentifier. Columns such as 名前 or länge_straße are
    // legitimate PostgreSQL identifiers.
    public static bool IsUnicodeIdentifier(string? identifier)
        => HasUnicodeIdentifierShape(identifier)
            && Encoding.UTF8.GetByteCount(identifier!) <= MaxIdentifierUtf8Bytes;

    // The character rules of IsUnicodeIdentifier without the byte limit, for names that
    // are bound as JSON keys rather than emitted as identifiers.
    public static bool HasUnicodeIdentifierShape(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return false;
        }

        var first = true;
        foreach (var rune in identifier.EnumerateRunes())
        {
            var allowed = first ? IsIdentifierStart(rune) : IsIdentifierPart(rune);
            if (!allowed)
            {
                return false;
            }

            first = false;
        }

        return true;
    }

    public static bool IsIdentifierStart(Rune rune)
        => rune.Value == '_' || Rune.IsLetter(rune);

    public static bool IsIdentifierPart(Rune rune)
        => IsIdentifierStart(rune)
            || Rune.IsDigit(rune)
            || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
}
