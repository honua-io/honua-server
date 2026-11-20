// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Honua.TestKit;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Reuses migrated control-plane schemas across cases. Building one runs the job substrate and
// control-plane migrations, each under the fixture's global schema-mutation lock, so cases from
// every class queue behind each other. The first freshly migrated schema of a database is recorded:
// its catalog (relations, columns in order, defaults, constraints, indexes, triggers and whether
// they are enabled, routines, views, policies and grants, with the schema name normalised), the rows
// the migrations insert and every sequence position. A finished case's schema is reused only while
// its catalog still matches; it is truncated, the recorded rows are inserted again and the sequences
// are put back, in one transaction, before the next case gets it. Any other schema is dropped as
// before, so a case that changes its schema never passes the change on. With a schema seed
// configured (HONUA_TEST_DB_SEED_PATH) nothing is pooled. At most MaxIdle schemas wait per database;
// with an external database (HONUA_TEST_DB_URL) those few stay behind when the test process ends.
internal sealed class ControlPlaneSchemaPool
{
    private const int MaxIdle = 8;

    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _idle = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FreshSchema> _fresh = new(StringComparer.Ordinal);

    internal static ControlPlaneSchemaPool Shared { get; } = new();

    // An idle migrated schema of this database, or null when the caller must build one.
    internal async Task<string?> TakeAsync(IIsolatedSchemaFixture fixture)
    {
        if (!_idle.TryGetValue(fixture.DataSource.ConnectionString, out var idle))
        {
            return null;
        }

        while (idle.TryTake(out var schema))
        {
            if (await ControlPlaneSchemaCatalog.ExistsAsync(fixture.DataSource, schema))
            {
                return schema;
            }
        }

        return null;
    }

    // Records a freshly migrated schema of this database, once per database.
    internal async Task RecordFreshAsync(IIsolatedSchemaFixture fixture, string schema)
    {
        var database = fixture.DataSource.ConnectionString;
        var seeded = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HONUA_TEST_DB_SEED_PATH"));
        if (!seeded && !_fresh.ContainsKey(database))
        {
            _fresh.TryAdd(database, await FreshSchema.ReadAsync(fixture.DataSource, schema));
        }
    }

    // True when the schema was restored to its freshly migrated state and kept for the next case;
    // false means the caller drops it.
    internal async Task<bool> TryReturnAsync(IIsolatedSchemaFixture fixture, string schema)
    {
        var database = fixture.DataSource.ConnectionString;
        var idle = _idle.GetOrAdd(database, _ => []);
        if (!_fresh.TryGetValue(database, out var fresh) || idle.Count >= MaxIdle)
        {
            return false;
        }

        try
        {
            var catalog = await ControlPlaneSchemaCatalog.DescribeAsync(fixture.DataSource, schema);
            if (!string.Equals(catalog, fresh.Catalog, StringComparison.Ordinal))
            {
                return false;
            }

            await fixture.ExecuteDdlUnderLockAsync(await fresh.RestoreSqlAsync(fixture.DataSource, schema));
        }
        catch (NpgsqlException)
        {
            // A connection a case left behind still holds a lock, or the rows cannot be put back.
            return false;
        }

        idle.Add(schema);
        return true;
    }

    // The rows and sequence positions of a freshly migrated schema of this database, as the content
    // description of ControlPlaneSchemaCatalog renders them.
    internal string FreshContent(IIsolatedSchemaFixture fixture)
        => _fresh[fixture.DataSource.ConnectionString].Content;

    internal async Task DropIdleAsync(IIsolatedSchemaFixture fixture)
    {
        if (_idle.TryGetValue(fixture.DataSource.ConnectionString, out var idle))
        {
            while (idle.TryTake(out var schema))
            {
                await fixture.DropSchemaAsync(schema);
            }
        }
    }

    private sealed record FreshSchema(string Catalog, IReadOnlyList<TableRows> Rows, IReadOnlyList<SequencePosition> Sequences)
    {
        public string Content => ControlPlaneSchemaCatalog.RenderContent(Rows, Sequences);

        public static async Task<FreshSchema> ReadAsync(NpgsqlDataSource dataSource, string schema)
            => new(
                await ControlPlaneSchemaCatalog.DescribeAsync(dataSource, schema),
                await ControlPlaneSchemaCatalog.ReadRowsAsync(dataSource, schema),
                await ControlPlaneSchemaCatalog.ReadSequencesAsync(dataSource, schema));

        public async Task<string> RestoreSqlAsync(NpgsqlDataSource dataSource, string schema)
        {
            var tables = await ControlPlaneSchemaCatalog.ListTablesAsync(dataSource, schema);
            var sql = new StringBuilder("BEGIN;\nSET LOCAL lock_timeout = '5s';\n");
            if (tables.Count > 0)
            {
                sql.Append("TRUNCATE TABLE ").AppendJoin(", ", tables.Select(table => Qualify(schema, table)))
                    .Append(" RESTART IDENTITY CASCADE;\n");
            }

            foreach (var rows in Rows)
            {
                var table = Qualify(schema, rows.Table);
                sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO {table} OVERRIDING SYSTEM VALUE SELECT * FROM json_populate_recordset(NULL::{table}, {Literal(rows.Json)});\n");
            }

            foreach (var sequence in Sequences)
            {
                var position = sequence.LastValue is { } last
                    ? last.ToString(CultureInfo.InvariantCulture) + ", true"
                    : sequence.StartValue.ToString(CultureInfo.InvariantCulture) + ", false";
                sql.Append(CultureInfo.InvariantCulture, $"SELECT setval({Literal(Qualify(schema, sequence.Name))}, {position});\n");
            }

            return sql.Append("COMMIT;").ToString();
        }

        private static string Qualify(string schema, string name) => $"\"{schema}\".\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

        private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
