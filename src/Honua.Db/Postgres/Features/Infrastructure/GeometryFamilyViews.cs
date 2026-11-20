// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Infrastructure;

// One view per geometry type over a mixed-geometry source table, so each type publishes
// as its own layer. An import that replaces the table detaches these views first (the
// replacement refuses to drop a table that other objects depend on) and attaches them
// again over the new table in the same transaction.
internal static class GeometryFamilyViews
{
    internal const int MaxIdentifierLength = 63;

    private const string MarkerPrefix = "honua-geometry-family:";

    private const string DependsOnTableSql = """
        EXISTS (
            SELECT 1
            FROM pg_rewrite r
            JOIN pg_depend d ON d.classid = 'pg_rewrite'::regclass AND d.objid = r.oid
            WHERE r.ev_class = c.oid
              AND d.refobjid = to_regclass(format('%I.%I', @schema, @table)))
        """;

    private static readonly string[] _families = ["Point", "LineString", "Polygon"];

    internal sealed record TableScope
    {
        public required NpgsqlConnection Connection { get; init; }

        public NpgsqlTransaction? Transaction { get; init; }

        public required string Schema { get; init; }

        public required string Table { get; init; }

        public NpgsqlCommand Command(string sql)
        {
            var command = new NpgsqlCommand(sql, Connection, Transaction);
            command.Parameters.AddWithValue("schema", Schema);
            command.Parameters.AddWithValue("table", Table);
            return command;
        }

        public NpgsqlCommand DdlCommand(string sql) => new(sql, Connection, Transaction);
    }

    internal sealed record FamilyFilter(string Family, string GeometryColumn);

    internal sealed record Detached(string ViewName, FamilyFilter Filter);

    internal static string ViewName(string table, string family)
    {
        var suffix = family switch
        {
            "Point" => "__honua_pt",
            "LineString" => "__honua_ln",
            "Polygon" => "__honua_pg",
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown geometry family.")
        };
        var stemLength = Math.Min(table.Length, MaxIdentifierLength - suffix.Length);
        return string.Concat(table.AsSpan(0, stemLength), suffix);
    }

    internal static string TypeList(string family) => family switch
    {
        "Point" => "'POINT','POINTZ','POINTM','POINTZM','MULTIPOINT','MULTIPOINTZ','MULTIPOINTM','MULTIPOINTZM'",
        "LineString" => "'LINESTRING','LINESTRINGZ','LINESTRINGM','LINESTRINGZM','MULTILINESTRING','MULTILINESTRINGZ','MULTILINESTRINGM','MULTILINESTRINGZM'",
        "Polygon" => "'POLYGON','POLYGONZ','POLYGONM','POLYGONZM','MULTIPOLYGON','MULTIPOLYGONZ','MULTIPOLYGONM','MULTIPOLYGONZM'",
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown geometry family.")
    };

    // The comment marks the view as Honua's and records the column it filters on, so an
    // import can recreate it without reading the layer catalog.
    internal static string CreateSql(string schema, string table, FamilyFilter filter)
    {
        var view = $"{SqlIdentifier.QuoteIdentifier(schema)}.{SqlIdentifier.QuoteIdentifier(ViewName(table, filter.Family))}";
        var marker = (MarkerPrefix + filter.GeometryColumn).Replace("'", "''", StringComparison.Ordinal);
        return $"""
            CREATE OR REPLACE VIEW {view} AS
            SELECT *
            FROM {SqlIdentifier.QuoteIdentifier(schema)}.{SqlIdentifier.QuoteIdentifier(table)}
            WHERE GeometryType({SqlIdentifier.QuoteIdentifier(filter.GeometryColumn)}) IN ({TypeList(filter.Family)})
            WITH LOCAL CHECK OPTION;
            COMMENT ON VIEW {view} IS '{marker}';
            """;
    }

    // True when a relation other than this table's own marked family view already holds the name.
    internal static async Task<bool> NameTakenByOtherAsync(
        TableScope scope,
        string viewName,
        CancellationToken cancellationToken)
    {
        await using var command = scope.Command($"""
            SELECT NOT (c.relkind = 'v'
                        AND starts_with(obj_description(c.oid, 'pg_class'), @marker)
                        AND {DependsOnTableSql})
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @view
            """);
        command.Parameters.AddWithValue("view", viewName);
        command.Parameters.AddWithValue("marker", MarkerPrefix);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    // Drops this table's family views and returns what recreating them needs.
    internal static async Task<IReadOnlyList<Detached>> DetachAsync(TableScope scope, CancellationToken cancellationToken)
    {
        var views = await ReadDependentViewsAsync(scope, cancellationToken).ConfigureAwait(false);
        foreach (var view in views)
        {
            await using var command = scope.DdlCommand(
                $"DROP VIEW {SqlIdentifier.QuoteIdentifier(scope.Schema)}.{SqlIdentifier.QuoteIdentifier(view.ViewName)}");
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return views;
    }

    // Recreates detached views over the replacement table, skipping any view whose
    // geometry column the replacement does not have.
    internal static async Task AttachAsync(
        TableScope scope,
        IReadOnlyList<Detached> detached,
        CancellationToken cancellationToken)
    {
        foreach (var view in detached)
        {
            if (!await HasColumnAsync(scope, view.Filter.GeometryColumn, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await using var command = scope.DdlCommand(CreateSql(scope.Schema, scope.Table, view.Filter));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Runs a statement that drops and replaces the table, keeping Honua's per-type views.
    internal static async Task ReplaceAsync(TableScope scope, string replaceSql, CancellationToken cancellationToken)
    {
        var detached = await DetachAsync(scope, cancellationToken).ConfigureAwait(false);
        await using (var command = scope.DdlCommand(replaceSql))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AttachAsync(scope, detached, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<Detached>> ReadDependentViewsAsync(TableScope scope, CancellationToken cancellationToken)
    {
        var names = _families.Select(family => ViewName(scope.Table, family)).ToArray();
        await using var command = scope.Command($"""
            SELECT c.relname, obj_description(c.oid, 'pg_class')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relkind = 'v'
              AND c.relname = ANY(@names)
              AND starts_with(obj_description(c.oid, 'pg_class'), @marker)
              AND {DependsOnTableSql}
            """);
        command.Parameters.AddWithValue("marker", MarkerPrefix);
        command.Parameters.Add("names", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = names;
        var views = new List<Detached>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var viewName = reader.GetString(0);
            var family = _families[Array.IndexOf(names, viewName)];
            views.Add(new Detached(viewName, new FamilyFilter(family, reader.GetString(1)[MarkerPrefix.Length..])));
        }

        return views;
    }

    private static async Task<bool> HasColumnAsync(TableScope scope, string column, CancellationToken cancellationToken)
    {
        await using var command = scope.Command("""
            SELECT EXISTS (
                SELECT 1
                FROM pg_attribute
                WHERE attrelid = to_regclass(format('%I.%I', @schema, @table))
                  AND attname = @column
                  AND attnum > 0
                  AND NOT attisdropped)
            """);
        command.Parameters.AddWithValue("column", column);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
