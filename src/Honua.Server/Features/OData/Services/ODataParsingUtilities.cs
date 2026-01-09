// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Provides shared utilities for parsing OData query parameters.
/// </summary>
internal static class ODataParsingUtilities
{
    /// <summary>
    /// Attempts to parse an optional integer parameter from a string value.
    /// </summary>
    /// <param name="value">The string value to parse.</param>
    /// <param name="parameterName">The name of the parameter for error messages.</param>
    /// <param name="parsed">The parsed integer value, or null if the input was null/empty.</param>
    /// <param name="errorMessage">The error message if parsing failed.</param>
    /// <returns>true if parsing succeeded or input was null/empty; false if parsing failed.</returns>
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

    /// <summary>
    /// Attempts to parse an optional boolean parameter from a string value.
    /// </summary>
    /// <param name="value">The string value to parse.</param>
    /// <param name="parameterName">The name of the parameter for error messages.</param>
    /// <param name="parsed">The parsed boolean value, or null if the input was null/empty.</param>
    /// <param name="errorMessage">The error message if parsing failed.</param>
    /// <returns>true if parsing succeeded or input was null/empty; false if parsing failed.</returns>
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
}
