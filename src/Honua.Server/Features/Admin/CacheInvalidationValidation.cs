// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static class CacheInvalidationValidation
{
    internal const int MaxFieldLength = 256;

    internal static bool TryValidateOptionalField(string? value, string fieldName, out string? error)
    {
        if (value is null)
        {
            error = null;
            return true;
        }

        if (value.Length > MaxFieldLength)
        {
            error = $"{fieldName} must be {MaxFieldLength} characters or fewer.";
            return false;
        }

        if (ContainsControlOrLineBreak(value))
        {
            error = $"{fieldName} must not contain control characters.";
            return false;
        }

        error = null;
        return true;
    }

    internal static bool ContainsControlOrLineBreak(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c) || c is '\u2028' or '\u2029')
            {
                return true;
            }
        }

        return false;
    }
}
