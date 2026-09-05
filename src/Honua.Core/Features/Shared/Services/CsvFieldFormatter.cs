// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Services;

/// <summary>
/// Encodes CSV cells while preserving the distinction between null and an empty string.
/// </summary>
public static class CsvFieldFormatter
{
    /// <summary>
    /// Writes null as an unquoted blank and quotes empty strings and CSV delimiters.
    /// </summary>
    public static string Escape(string? value)
    {
        if (value is null)
            return string.Empty;
        if (value.Length == 0)
            return "\"\"";
        if (value.AsSpan().IndexOfAny(',', '"', '\n') >= 0 || value.Contains('\r'))
            return string.Concat("\"", value.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
        return value;
    }
}
