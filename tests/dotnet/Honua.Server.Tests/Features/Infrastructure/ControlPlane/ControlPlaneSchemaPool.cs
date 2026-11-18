// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.TestKit;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Reuses migrated control-plane schemas across cases. Building one runs the job substrate and
// control-plane migrations, each under the fixture's global schema-mutation lock, so cases from
// every class queue behind each other. A finished case's schema is reused only while its catalog
// still matches a freshly migrated one (relations, columns in order, defaults, constraints,
// indexes, triggers and whether they are enabled, routines, views, policies and grants, with the
// schema name normalised); its rows are truncated and every sequence restarted before the next
// case gets it. Any other schema is dropped as before, so a case that changes its schema never
// passes the change on. With a schema seed configured (HONUA_TEST_DB_SEED_PATH) a fresh schema holds
// seed rows that truncation would remove, so nothing is pooled.
internal static class ControlPlaneSchemaPool
{
    private const string SchemaPlaceholder = "<schema>";

    private const string CatalogSql = """
        WITH ns AS (SELECT oid FROM pg_namespace WHERE nspname = @schema)
        SELECT coalesce(string_agg(entry, E'\n' ORDER BY entry), '')
        FROM (
            SELECT format('relation %s %s %s %s %s %s', c.relname, c.relkind, c.relpersistence,
                c.relrowsecurity, c.relforcerowsecurity, coalesce(c.relacl::text, ''))
            FROM pg_class c WHERE c.relnamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('column %s.%s %s %s %s %s %s %s', c.relname, a.attnum, a.attname,
                format_type(a.atttypid, a.atttypmod), a.attnotnull, a.attidentity, a.attgenerated,
                coalesce(pg_get_expr(d.adbin, d.adrelid), ''))
            FROM pg_class c
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
            WHERE c.relnamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('constraint %s.%s %s %s', c.relname, k.conname, pg_get_constraintdef(k.oid), k.convalidated)
            FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid
            WHERE c.relnamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('index %s', pg_get_indexdef(i.indexrelid))
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relnamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('trigger %s %s', pg_get_triggerdef(t.oid), t.tgenabled)
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
            WHERE c.relnamespace = (SELECT oid FROM ns) AND NOT t.tgisinternal
            UNION ALL
            SELECT format('policy %s.%s %s %s %s %s', c.relname, p.polname, p.polcmd, p.polpermissive,
                coalesce(pg_get_expr(p.polqual, p.polrelid), ''), coalesce(pg_get_expr(p.polwithcheck, p.polrelid), ''))
            FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
            WHERE c.relnamespace = (SELECT oid FROM ns)
            UNION ALL
            SELECT format('routine %s %s', pg_get_functiondef(p.oid), coalesce(p.proacl::text, ''))
            FROM pg_proc p WHERE p.pronamespace = (SELECT oid FROM ns) AND p.prokind IN ('f', 'p')
            UNION ALL
            SELECT format('view %s %s', c.relname, pg_get_viewdef(c.oid))
            FROM pg_class c WHERE c.relnamespace = (SELECT oid FROM ns) AND c.relkind IN ('v', 'm')
            UNION ALL
            SELECT format('type %s %s', t.typname, t.typtype)
            FROM pg_type t WHERE t.typnamespace = (SELECT oid FROM ns) AND t.typtype IN ('d', 'e', 'm', 'r')
        ) AS catalog(entry)
        """;

    private const string TablesSql = """
        SELECT coalesce(string_agg(format('%I.%I', n.nspname, c.relname), ', '), '')
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relkind IN ('r', 'p')
        """;

    private static readonly ConcurrentDictionary<string, ConcurrentBag<string>> _idle = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> _migratedCatalogs = new(StringComparer.Ordinal);

    // An idle migrated schema of this database, or null when the caller must build one.
    internal static async Task<string?> TakeAsync(IIsolatedSchemaFixture fixture)
    {
        if (!_idle.TryGetValue(fixture.DataSource.ConnectionString, out var idle))
        {
            return null;
        }

        while (idle.TryTake(out var schema))
        {
            if (await SchemaExistsAsync(fixture.DataSource, schema))
            {
                return schema;
            }
        }

        return null;
    }

    // Records what a freshly migrated schema of this database looks like, once per database.
    internal static async Task RecordMigratedAsync(IIsolatedSchemaFixture fixture, string schema)
    {
        var database = fixture.DataSource.ConnectionString;
        var seeded = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HONUA_TEST_DB_SEED_PATH"));
        if (!seeded && !_migratedCatalogs.ContainsKey(database))
        {
            _migratedCatalogs.TryAdd(database, await DescribeCatalogAsync(fixture.DataSource, schema));
        }
    }

    // True when the schema was emptied and kept for the next case; false means the caller drops it.
    internal static async Task<bool> TryReturnAsync(IIsolatedSchemaFixture fixture, string schema)
    {
        var database = fixture.DataSource.ConnectionString;
        if (!_migratedCatalogs.TryGetValue(database, out var migrated)
            || !string.Equals(await DescribeCatalogAsync(fixture.DataSource, schema), migrated, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            await ResetRowsAsync(fixture, schema);
        }
        catch (NpgsqlException)
        {
            // A connection a case left behind still holds a table lock: drop the schema instead.
            return false;
        }

        _idle.GetOrAdd(database, _ => []).Add(schema);
        return true;
    }

    private static async Task ResetRowsAsync(IIsolatedSchemaFixture fixture, string schema)
    {
        var tables = await ScalarAsync(fixture.DataSource, TablesSql, schema);
        var truncate = tables.Length == 0 ? string.Empty : $"TRUNCATE TABLE {tables} RESTART IDENTITY CASCADE;";
        var literal = schema.Replace("'", "''", StringComparison.Ordinal);
        await fixture.ExecuteDdlUnderLockAsync($"""
            BEGIN;
            SET LOCAL lock_timeout = '5s';
            {truncate}
            SELECT setval(format('%I.%I', schemaname, sequencename)::regclass, start_value, false)
            FROM pg_sequences WHERE schemaname = '{literal}';
            COMMIT;
            """);
    }

    private static async Task<string> DescribeCatalogAsync(NpgsqlDataSource dataSource, string schema)
        => (await ScalarAsync(dataSource, CatalogSql, schema)).Replace(schema, SchemaPlaceholder, StringComparison.Ordinal);

    private static async Task<bool> SchemaExistsAsync(NpgsqlDataSource dataSource, string schema)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)");
        command.Parameters.AddWithValue("schema", schema);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<string> ScalarAsync(NpgsqlDataSource dataSource, string sql, string schema)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("schema", schema);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
