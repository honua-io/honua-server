// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Publishing;

/// <summary>
/// Resolves one existing authoritative promotion family without moving or creating records.
/// Migration 082 historically created this family in honua even with Database:Schema set.
/// </summary>
internal static class PostgresPromotionSchema
{
    public static async Task<string> ResolveAsync(
        NpgsqlConnection connection,
        string? configuredSchema,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        var schema = string.IsNullOrWhiteSpace(configuredSchema) ? "honua" : configuredSchema;
        _ = SchemaSearchPath.ValidateAndQuote(schema);
        const string sql = """
            SELECT n.nspname, c.relkind::text,
                   EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = @configured)
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN (@configured, 'honua')
              AND c.relname IN ('promotion_deployments', 'promotion_published_services');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("configured", schema);
        var configuredCount = 0;
        var legacyCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.GetBoolean(2))
            {
                throw new InvalidOperationException("Canonical promotion family configured schema is absent.");
            }
            if (reader.GetString(1) is not ("r" or "p"))
            {
                throw new InvalidOperationException("Canonical promotion family contains a non-table object.");
            }
            if (string.Equals(reader.GetString(0), schema, StringComparison.Ordinal))
            {
                configuredCount++;
            }
            else
            {
                legacyCount++;
            }
        }

        if (configuredCount == 2 && legacyCount == 0)
        {
            return schema;
        }
        if (configuredCount == 0 && legacyCount == 2)
        {
            return "honua";
        }
        throw new InvalidOperationException(
            "Canonical promotion family is absent, partial, or ambiguous; reconcile its physical schema before serving.");
    }
}
