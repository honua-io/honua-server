// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

internal sealed record TableRows(string Table, string Json);

internal sealed record SequencePosition(string Name, long? LastValue, long StartValue);

// Reads what ControlPlaneSchemaPool compares and restores: a schema's catalog, with the schema name
// replaced so two schemas built by the same migrations describe the same, and its rows and
// sequence positions.
internal static class ControlPlaneSchemaCatalog
{
    private const string CatalogSql = """
        WITH ns AS (SELECT oid FROM pg_namespace WHERE nspname = @schema),
        catalog(entry) AS (
            SELECT format('relation %s %s %s %s %s %s', c.relname, c.relkind, c.relpersistence,
                c.relrowsecurity, c.relforcerowsecurity, coalesce(c.relacl::text, ''))
            FROM pg_class c WHERE c.relnamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('column %s.%s %s %s %s %s %s %s %s', c.relname, a.attnum, a.attname,
                format_type(a.atttypid, a.atttypmod), a.attcollation, a.attnotnull, a.attidentity,
                a.attgenerated, coalesce(pg_get_expr(d.adbin, d.adrelid), ''))
            FROM pg_class c
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
            WHERE c.relnamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('constraint %s %s %s %s', k.conrelid::regclass, k.conname, pg_get_constraintdef(k.oid), k.convalidated)
            FROM pg_constraint k WHERE k.connamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('index %s', pg_get_indexdef(i.indexrelid))
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relnamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('trigger %s %s', pg_get_triggerdef(t.oid), t.tgenabled)
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
            WHERE c.relnamespace = (SELECT oid FROM ns) AND NOT t.tgisinternal
            UNION ALL
            SELECT format('rule %s', pg_get_ruledef(r.oid))
            FROM pg_rewrite r JOIN pg_class c ON c.oid = r.ev_class
            WHERE c.relnamespace = (SELECT oid FROM ns) AND c.relkind IN ('r', 'p')
            UNION ALL
            SELECT format('policy %s.%s %s %s %s %s', c.relname, p.polname, p.polcmd, p.polpermissive,
                coalesce(pg_get_expr(p.polqual, p.polrelid), ''), coalesce(pg_get_expr(p.polwithcheck, p.polrelid), ''))
            FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
            WHERE c.relnamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('routine %s %s %s %s', p.oid::regprocedure, p.prokind,
                md5(replace(p.prosrc, @schema, '<schema>')), coalesce(p.proacl::text, ''))
            FROM pg_proc p WHERE p.pronamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('view %s %s', c.relname, pg_get_viewdef(c.oid))
            FROM pg_class c WHERE c.relnamespace = (SELECT oid FROM ns) AND c.relkind IN ('v', 'm')
            UNION ALL
            SELECT format('sequence %s %s %s %s %s %s %s', s.sequencename, s.data_type, s.start_value,
                s.increment_by, s.min_value, s.max_value, s.cycle)
            FROM pg_sequences s WHERE s.schemaname = @schema
            UNION ALL
            SELECT format('type %s %s %s', t.typname, t.typtype,
                coalesce((SELECT string_agg(e.enumlabel, ',' ORDER BY e.enumsortorder) FROM pg_enum e WHERE e.enumtypid = t.oid), ''))
            FROM pg_type t WHERE t.typnamespace = (SELECT oid FROM ns) AND t.typtype IN ('c', 'd', 'e', 'm', 'r')
        )
        SELECT coalesce(string_agg(normalized, E'\n' ORDER BY normalized COLLATE "C"), '')
        FROM (SELECT replace(entry, @schema, '<schema>') AS normalized FROM catalog) AS described
        """;

    private const string TablesSql = """
        SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relkind IN ('r', 'p') ORDER BY c.relname COLLATE "C"
        """;

    private const string SequencesSql = """
        SELECT sequencename, last_value, start_value FROM pg_sequences
        WHERE schemaname = @schema ORDER BY sequencename COLLATE "C"
        """;

    internal static async Task<bool> ExistsAsync(NpgsqlDataSource dataSource, string schema)
    {
        await using var command = Command(dataSource, "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)", schema);
        return await command.ExecuteScalarAsync() is true;
    }

    internal static async Task<string> DescribeAsync(NpgsqlDataSource dataSource, string schema)
    {
        await using var command = Command(dataSource, CatalogSql, schema);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task<IReadOnlyList<string>> ListTablesAsync(NpgsqlDataSource dataSource, string schema)
    {
        await using var command = Command(dataSource, TablesSql, schema);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    // Every non-empty table's rows as JSON, ordered so two equal tables render the same.
    internal static async Task<IReadOnlyList<TableRows>> ReadRowsAsync(NpgsqlDataSource dataSource, string schema)
    {
        var rows = new List<TableRows>();
        foreach (var table in await ListTablesAsync(dataSource, schema))
        {
            var qualified = $"\"{schema}\".\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            await using var command = dataSource.CreateCommand(
                $"SELECT json_agg(t ORDER BY t::text COLLATE \"C\")::text FROM {qualified} AS t");
            if (await command.ExecuteScalarAsync() is string json)
            {
                rows.Add(new TableRows(table, json));
            }
        }

        return rows;
    }

    internal static async Task<IReadOnlyList<SequencePosition>> ReadSequencesAsync(NpgsqlDataSource dataSource, string schema)
    {
        await using var command = Command(dataSource, SequencesSql, schema);
        await using var reader = await command.ExecuteReaderAsync();
        var sequences = new List<SequencePosition>();
        while (await reader.ReadAsync())
        {
            long? lastValue = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            sequences.Add(new SequencePosition(reader.GetString(0), lastValue, reader.GetInt64(2)));
        }

        return sequences;
    }

    internal static async Task<string> DescribeContentAsync(NpgsqlDataSource dataSource, string schema)
        => RenderContent(await ReadRowsAsync(dataSource, schema), await ReadSequencesAsync(dataSource, schema));

    internal static string RenderContent(IReadOnlyList<TableRows> rows, IReadOnlyList<SequencePosition> sequences)
    {
        var content = new StringBuilder();
        foreach (var table in rows)
        {
            content.Append(CultureInfo.InvariantCulture, $"rows {table.Table} {table.Json}\n");
        }

        foreach (var sequence in sequences)
        {
            content.Append(CultureInfo.InvariantCulture, $"sequence {sequence.Name} {sequence.LastValue} {sequence.StartValue}\n");
        }

        return content.ToString();
    }

    private static NpgsqlCommand Command(NpgsqlDataSource dataSource, string sql, string schema)
    {
        var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("schema", schema);
        return command;
    }
}
