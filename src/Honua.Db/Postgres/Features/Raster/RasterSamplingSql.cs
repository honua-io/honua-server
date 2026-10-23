// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Raster;

internal enum RasterSamplerKind
{
    NativeWindow,
    SeparableNearest,
    GeneralNearest,
}

/// <summary>
/// Installs one SQL function per sampler so COALESCE plans a call, not every fallback body.
/// <c>SET search_path</c> keeps the function from being inlined; it does not change JIT policy.
/// </summary>
internal static class RasterSamplingSql
{
    internal const string MigrationResourceName = "Honua.Postgres.Migrations.008_AddRasterSamplingFunctions.sql";
    private const string FunctionSchema = "honua_raster";

    private readonly record struct Definition(RasterSamplerKind Kind, bool Coverage, string Name, string Token);

    private static readonly Definition[] Definitions =
    [
        new(RasterSamplerKind.NativeWindow, false, "native_window_v1", "HonuaRasterNativeWindowV1"),
        new(RasterSamplerKind.NativeWindow, true, "native_window_coverage_v1", "HonuaRasterNativeWindowCoverageV1"),
        new(RasterSamplerKind.SeparableNearest, false, "separable_nearest_v1", "HonuaRasterSeparableNearestV1"),
        new(RasterSamplerKind.SeparableNearest, true, "separable_nearest_coverage_v1", "HonuaRasterSeparableNearestCoverageV1"),
        new(RasterSamplerKind.GeneralNearest, false, "general_nearest_v1", "HonuaRasterGeneralNearestV1"),
        new(RasterSamplerKind.GeneralNearest, true, "general_nearest_coverage_v1", "HonuaRasterGeneralNearestCoverageV1"),
    ];

    internal static string FunctionCall(
        RasterSamplerKind kind, string rasterExpression, string gridExpression, bool preserveCoverageMask)
    {
        var name = Definitions.Single(definition => definition.Kind == kind && definition.Coverage == preserveCoverageMask).Name;
        return $"{FunctionSchema}.{name}({rasterExpression}, {gridExpression})";
    }

    internal static IEnumerable<(string Name, string Body)> MigrationVariables()
        => Definitions.Select(definition => (definition.Token, Body(definition)));

    internal static string ReadBoundMigration()
    {
        using var stream = typeof(RasterSamplingSql).Assembly.GetManifestResourceStream(MigrationResourceName)
            ?? throw new InvalidOperationException($"Canonical raster sampling resource {MigrationResourceName} is missing.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return Bind(reader.ReadToEnd());
    }

    internal static async Task VerifyFunctionsAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.proname, p.provolatile::text, p.prosecdef, l.lanname, p.prosrc, p.proconfig
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_catalog.pg_language l ON l.oid = p.prolang
            WHERE n.nspname = @schema AND p.proname = ANY(@names)
              AND p.pronargs = 2
              AND p.proargtypes[0] = pg_catalog.to_regtype('public.raster')
              AND p.proargtypes[1] = pg_catalog.to_regtype('public.raster')
              AND p.prorettype = pg_catalog.to_regtype('public.raster')
            """;
        var schema = command.CreateParameter();
        schema.ParameterName = "schema";
        schema.Value = FunctionSchema;
        command.Parameters.Add(schema);
        var names = command.CreateParameter();
        names.ParameterName = "names";
        names.Value = Definitions.Select(definition => definition.Name).ToArray();
        if (names is NpgsqlParameter npgsqlNames)
            npgsqlNames.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
        command.Parameters.Add(names);

        var found = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var definition = Definitions.Single(item => item.Name == name);
            var expected = "SELECT " + Body(definition).Trim() + ";";
            var settings = reader.IsDBNull(5) ? [] : (string[])reader.GetValue(5);
            if (reader.GetString(1) != "s" || reader.GetBoolean(2) || reader.GetString(3) != "sql" ||
                !settings.Contains("search_path=pg_catalog, public", StringComparer.Ordinal) ||
                !string.Equals(Normalize(reader.GetString(4)), Normalize(expected), StringComparison.Ordinal))
            {
                throw MissingOrIncompatible();
            }

            found.Add(name);
        }

        if (found.Count != Definitions.Length)
            throw MissingOrIncompatible();
    }

    private static string Bind(string contents)
    {
        foreach (var definition in Definitions)
        {
            var token = "$" + definition.Token + "$";
            if (!contents.Contains(token, StringComparison.Ordinal))
                throw new InvalidOperationException("The canonical raster sampling migration is missing a versioned body binding.");
            contents = contents.Replace(token, Body(definition), StringComparison.Ordinal);
        }

        return contents;
    }

    private static string Body(Definition definition) => definition.Kind switch
    {
        RasterSamplerKind.NativeWindow => RasterNativeWindowSql.Definition("p_raster", "p_grid", definition.Coverage),
        RasterSamplerKind.SeparableNearest => RasterSeparableNearestSql.Definition("p_raster", "p_grid", definition.Coverage),
        RasterSamplerKind.GeneralNearest => RasterGeneralNearestSql.Definition("p_raster", "p_grid", definition.Coverage),
        _ => throw new ArgumentOutOfRangeException(nameof(definition)),
    };

    private static string Normalize(string sql) => sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static DatabaseSchemaFloorException MissingOrIncompatible()
        => new(MigrationResourceName, DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema,
            "The versioned raster sampling functions are missing or differ from their packaged definition. " +
            "Reconcile the canonical migration before serving raster requests.");
}
