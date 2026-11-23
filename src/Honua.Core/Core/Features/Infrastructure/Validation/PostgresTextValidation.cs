// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text;

namespace Honua.Core.Features.Infrastructure.Validation;

// PostgreSQL text and jsonb reject the NUL character and unpaired surrogates, and an over-long value
// in a btree index fails the insert, so caller text bound for either is checked before the first
// write. Single-line values (identifiers, names) refuse every control character; multi-line values
// (notes, comments) keep line breaks and tabs. Each method returns a problem detail naming the field,
// or null when the value is absent or storable.
public static class PostgresTextValidation
{
    public static string? FindSingleLineError(string field, string? value, int maxLength)
        => FindError(field, value, maxLength, static _ => false);

    public static string? FindMultiLineError(string field, string? value, int maxLength)
        => FindError(field, value, maxLength, static rune => rune.Value is '\n' or '\r' or '\t');

    private static string? FindError(string field, string? value, int maxLength, Func<Rune, bool> allowedControl)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            return $"Field '{field}' must be at most {maxLength} characters.";
        }

        return IsStorable(value, allowedControl)
            ? null
            : $"Field '{field}' must not contain control characters or unpaired surrogates.";
    }

    private static bool IsStorable(string value, Func<Rune, bool> allowedControl)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done
                || (Rune.IsControl(rune) && !allowedControl(rune)))
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }
}
