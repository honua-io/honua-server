// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Oracle.Features.FeatureStore.Services;

/// <summary>
/// Validates and double-quotes Oracle identifiers (schema, table, column).
/// </summary>
/// <remarks>
/// Oracle quoted identifiers (<c>"…"</c>) are case-preserving and may contain other characters,
/// but the provider deliberately restricts configured identifiers to the regular form
/// (start with letter or underscore, followed by letters, digits, or underscores) so configured
/// names are unambiguous and SQL-injection-safe.
/// </remarks>
internal static partial class OracleIdentifier
{
    private static readonly Regex _identifierRegex = IdentifierPattern();

    /// <summary>
    /// Returns true when the identifier matches the safe-identifier allow-list.
    /// </summary>
    public static bool IsValid(string? value)
        => !string.IsNullOrWhiteSpace(value) && _identifierRegex.IsMatch(value);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when the identifier is not safe.
    /// </summary>
    public static void EnsureValid(string value, string label)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"Invalid Oracle {label} '{value}'. Identifiers must match [A-Za-z_][A-Za-z0-9_]*.");
        }
    }

    /// <summary>
    /// Double-quotes a previously validated identifier. Any embedded double-quote characters in
    /// the identifier are doubled to keep parsing robust even though the allow-list rejects them.
    /// </summary>
    public static string Quote(string identifier)
    {
        EnsureValid(identifier, "identifier");
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
