// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

internal static partial class SchemaSearchPath
{
    private static readonly Regex _schemaNameRegex = SchemaNamePattern();

    public static async Task ApplyAsync(
        NpgsqlConnection connection,
        string? schemaName,
        string? connectionStringDefaultSchema = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            return;
        }

        var sanitized = schemaName.Trim();
        if (!_schemaNameRegex.IsMatch(sanitized))
        {
            throw new InvalidOperationException($"Invalid schema name '{schemaName}'.");
        }

        // Skip the SET round-trip when the connection string already embeds
        // this schema via the Options parameter (Tier 3 optimization).
        if (!string.IsNullOrWhiteSpace(connectionStringDefaultSchema) &&
            string.Equals(sanitized, connectionStringDefaultSchema.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO \"{sanitized}\", public;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a safe SQL identifier
    /// (starts with a letter or underscore, followed by letters, digits, or underscores).
    /// </summary>
    public static bool IsValidIdentifier(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && _schemaNameRegex.IsMatch(value);
    }

    /// <summary>
    /// Validates a schema name against the safe identifier pattern.
    /// </summary>
    public static string ValidateAndQuote(string schemaName)
    {
        var sanitized = schemaName.Trim();
        if (!_schemaNameRegex.IsMatch(sanitized))
        {
            throw new InvalidOperationException($"Invalid schema name '{schemaName}'.");
        }

        return $"\"{sanitized}\"";
    }

    /// <summary>
    /// Builds a fully qualified table name with schema validation.
    /// Defaults to "honua" schema if none is specified.
    /// </summary>
    public static string QualifyTable(string tableName, string? schemaName = null)
    {
        var quotedSchema = ValidateAndQuote(
            string.IsNullOrWhiteSpace(schemaName) ? "honua" : schemaName);
        return $"{quotedSchema}.{tableName}";
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNamePattern();
}
