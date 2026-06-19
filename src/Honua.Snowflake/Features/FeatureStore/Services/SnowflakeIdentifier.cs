// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Snowflake.Features.FeatureStore.Services;

/// <summary>
/// Validates and double-quotes Snowflake identifiers (database, schema, table, column).
/// </summary>
/// <remarks>
/// <para>Snowflake identifier rules (the regular form supported here): start with a letter or
/// underscore, followed by letters, digits, or underscores. Double-quoted identifiers can
/// technically contain other characters, but the provider deliberately restricts configured
/// identifiers to the regular form so they are unambiguous and SQL-injection-safe.</para>
/// <para><strong>Case sensitivity:</strong> Snowflake folds <em>unquoted</em> identifiers to
/// upper case but treats <em>double-quoted</em> identifiers as case-sensitive and exactly as
/// written. The provider always double-quotes identifiers, so configured names are
/// case-preserving — configure them exactly as they exist in Snowflake. Conventional unquoted
/// objects (e.g. <c>CREATE TABLE parcels (...)</c>) are stored upper-case in the catalog, so
/// configure them as <c>PARCELS</c>.</para>
/// </remarks>
internal static partial class SnowflakeIdentifier
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
                $"Invalid Snowflake {label} '{value}'. Identifiers must match [A-Za-z_][A-Za-z0-9_]*.");
        }
    }

    /// <summary>
    /// Double-quotes a previously validated identifier. Any embedded double-quotes are doubled to
    /// keep parsing robust even though the allow-list rejects them.
    /// </summary>
    public static string Quote(string identifier)
    {
        EnsureValid(identifier, "identifier");
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
