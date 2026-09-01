// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

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

        // Skip the SET round-trip when the data source already pins this schema as
        // the default search_path — applied by PostgresDataSourceFactory via the
        // physical-connection initializer (or the Options startup parameter when
        // multiplexing is enabled). Tier 3 optimization.
        if (!string.IsNullOrWhiteSpace(connectionStringDefaultSchema) &&
            string.Equals(sanitized, connectionStringDefaultSchema.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        // SET does not accept a bound identifier, so use PostgreSQL's set_config value API.
        // The provider quotes the allow-listed schema as an identifier, while the entire
        // search_path value is still transmitted as data rather than interpolated SQL.
        command.CommandText = "SELECT set_config('search_path', @searchPath, false);";
        command.Parameters.AddWithValue("searchPath", BuildSearchPathValue(sanitized));
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

        return QuoteIdentifier(sanitized);
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

    private static string QuoteIdentifier(string identifier)
    {
        using var commandBuilder = new NpgsqlCommandBuilder();
        return commandBuilder.QuoteIdentifier(identifier);
    }

    internal static string BuildSearchPathValue(string schemaName)
    {
        return $"{ValidateAndQuote(schemaName)}, public";
    }
}
