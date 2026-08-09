// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.MySql.Features.Infrastructure;

/// <summary>
/// MySQL/MariaDB identifier helpers. Identifiers are backtick-quoted and embedded
/// backticks are doubled per MySQL convention.
/// </summary>
internal static partial class MySqlIdentifier
{
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleIdentifierRegex();

    /// <summary>Quotes an identifier with MySQL backticks.</summary>
    public static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            throw new ArgumentException("Identifier must not be empty.", nameof(identifier));
        }

        return $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    }

    /// <summary>
    /// Validates that <paramref name="fieldName"/> is a simple identifier
    /// (letters, digits, underscores; not starting with a digit). Throws
    /// <see cref="ArgumentException"/> otherwise. Used wherever user-supplied
    /// field names enter SQL.
    /// </summary>
    public static void ValidateFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || !SimpleIdentifierRegex().IsMatch(fieldName))
        {
            throw new ArgumentException($"Invalid field name: {fieldName}");
        }
    }
}
