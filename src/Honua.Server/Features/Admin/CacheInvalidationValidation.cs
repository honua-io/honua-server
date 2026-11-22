// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static class CacheInvalidationValidation
{
    internal const int MaxFieldLength = 256;

    internal static string? ValidateOptionalField(string? value, string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > MaxFieldLength)
        {
            return $"{fieldName} must be {MaxFieldLength} characters or fewer.";
        }

        if (ContainsControlOrLineBreak(value))
        {
            return $"{fieldName} must not contain control characters.";
        }

        return null;
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
