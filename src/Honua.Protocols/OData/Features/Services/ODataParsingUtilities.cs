// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Protocols.OData.Services;

internal static class ODataParsingUtilities
{
    public static bool TryParseOptionalInt(
        string? value,
        string parameterName,
        out int? parsed,
        out string? errorMessage)
    {
        parsed = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            errorMessage = $"{parameterName} must be a valid integer.";
            return false;
        }

        parsed = result;
        return true;
    }

    public static bool TryParseOptionalBool(
        string? value,
        string parameterName,
        out bool? parsed,
        out string? errorMessage)
    {
        parsed = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!bool.TryParse(value, out var result))
        {
            errorMessage = $"{parameterName} must be 'true' or 'false'.";
            return false;
        }

        parsed = result;
        return true;
    }

    public static bool HasEmptyCommaSeparatedToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Split(',', StringSplitOptions.None).Any(token => token.Trim().Length == 0);
    }
}
