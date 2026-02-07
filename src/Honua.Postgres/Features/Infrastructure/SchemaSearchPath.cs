// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

internal static partial class SchemaSearchPath
{
    private static readonly Regex _schemaNameRegex = SchemaNamePattern();

    public static async Task ApplyAsync(NpgsqlConnection connection, string? schemaName, CancellationToken cancellationToken = default)
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

        await using var command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO \"{sanitized}\", public;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNamePattern();
}
