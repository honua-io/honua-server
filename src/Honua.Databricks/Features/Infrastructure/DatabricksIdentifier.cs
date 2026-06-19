// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Databricks.Features.Infrastructure;

/// <summary>
/// Databricks SQL identifier helpers. Identifiers are backtick-quoted and embedded
/// backticks are doubled, matching Databricks SQL (Spark SQL) conventions.
/// </summary>
internal static partial class DatabricksIdentifier
{
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleIdentifierRegex();

    /// <summary>Quotes a single identifier with Databricks backticks.</summary>
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
    /// <see cref="ArgumentException"/> otherwise. Used wherever a configured
    /// column or table name enters generated SQL.
    /// </summary>
    public static void ValidateIdentifier(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || !SimpleIdentifierRegex().IsMatch(fieldName))
        {
            throw new ArgumentException($"Invalid Databricks identifier: {fieldName}");
        }
    }
}
