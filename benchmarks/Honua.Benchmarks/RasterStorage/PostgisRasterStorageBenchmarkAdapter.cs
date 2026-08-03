// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Npgsql;

namespace Honua.Benchmarks.RasterStorage;

internal sealed record PostgisRasterStorageBenchmarkOptions(
    string ConnectionString,
    IReadOnlyList<string> FixtureIds,
    int WarmupCount,
    int SampleCount,
    int BlockSize,
    int ConcurrentTenants,
    bool KeepScratchSchema);

internal sealed class PostgisRasterStorageBenchmarkAdapter(PostgisRasterStorageBenchmarkOptions options)
{
    private readonly RasterStorageProtocolDefinition _protocol = RasterStorageProtocol.Create();

    public async Task<RasterStorageBenchmarkRun> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var results = new List<RasterStorageWorkloadResult>();
        await using var dataSource = NpgsqlDataSource.Create(options.ConnectionString);
        await EnsurePostgisRasterAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var (postgresVersion, postgisVersion) = await ReadVersionsAsync(dataSource, cancellationToken).ConfigureAwait(false);

        var selectedFixtures = SelectFixtures();
        foreach (var layout in new[]
                 {
                     RasterStorageLayout.PostgisMonolithicExternal,
                     RasterStorageLayout.PostgisTiled,
                 })
        {
            foreach (var fixture in selectedFixtures)
            {
                var schema = $"honua_rast_bench_{runId[..8]}_{LayoutToken(layout)}_{FixtureToken(fixture.Id)}";
                try
                {
                    await CreateScratchSchemaAsync(dataSource, schema, cancellationToken).ConfigureAwait(false);
                    var ingest = await IngestFixtureAsync(dataSource, schema, layout, fixture, cancellationToken)
                        .ConfigureAwait(false);
                    results.Add(await CreateIngestResultAsync(
                            dataSource, schema, layout, fixture, ingest, cancellationToken)
                        .ConfigureAwait(false));

                    foreach (var workload in Enum.GetValues<RasterStorageWorkload>())
                    {
                        if (workload == RasterStorageWorkload.Ingest)
                        {
                            continue;
                        }

                        var cell = _protocol.Cells.Single(candidate =>
                            candidate.Layout == layout && candidate.Workload == workload);
                        if (cell.Support == BenchmarkSupport.ExternalStep)
                        {
                            results.Add(CreateNonCompleted(
                                layout,
                                fixture.Id,
                                workload,
                                BenchmarkResultStatus.ExternalStepRequired,
                                cell.Reason));
                            continue;
                        }

                        if (fixture.AlignmentExpectation == GridExpectation.Misaligned && RequiresAlignedGrid(workload))
                        {
                            results.Add(CreateNonCompleted(
                                layout,
                                fixture.Id,
                                workload,
                                BenchmarkResultStatus.Ineligible,
                                "The fixture fails the canonical grid contract; normalize into a versioned target before this logical-mosaic workload."));
                            continue;
                        }

                        results.Add(await RunWorkloadSafelyAsync(
                                dataSource, schema, layout, fixture, workload, cancellationToken)
                            .ConfigureAwait(false));
                    }
                }
                finally
                {
                    if (!options.KeepScratchSchema)
                    {
                        await DropScratchSchemaAsync(dataSource, schema, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }

        return new RasterStorageBenchmarkRun(
            RasterStorageProtocol.Version,
            runId,
            startedAt,
            DateTimeOffset.UtcNow,
            new RasterStorageEnvironment(
                Environment.OSVersion.ToString(),
                Environment.Version.ToString(),
                Environment.ProcessorCount,
                Environment.GetEnvironmentVariable("GITHUB_SHA"),
                postgresVersion,
                postgisVersion,
                null,
                "Benchmark-only isolated scratch schemas; pg_stat_database deltas require an otherwise idle benchmark database."),
            results);
    }

    private IReadOnlyList<RasterFixtureDefinition> SelectFixtures()
    {
        var selected = options.FixtureIds.Count == 0
            ? _protocol.Fixtures
            : _protocol.Fixtures.Where(fixture => options.FixtureIds.Contains(fixture.Id, StringComparer.Ordinal)).ToArray();
        var missing = options.FixtureIds.Except(selected.Select(fixture => fixture.Id), StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException($"Unknown raster fixture(s): {string.Join(", ", missing)}.");
        }

        return selected;
    }

    private async Task<TimeSpan> IngestFixtureAsync(
        NpgsqlDataSource dataSource,
        string schema,
        RasterStorageLayout layout,
        RasterFixtureDefinition fixture,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"""
                CREATE TABLE {Quote(schema)}.rasters (
                    tile_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    fixture_id text NOT NULL,
                    scene_id integer NOT NULL,
                    rast raster NOT NULL
                );
                ALTER TABLE {Quote(schema)}.rasters ALTER COLUMN rast SET STORAGE EXTERNAL;
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < fixture.Scenes.Count; index++)
        {
            await InsertSceneAsync(connection, schema, layout, fixture, index, cancellationToken).ConfigureAwait(false);
        }

        await using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.CommandText = $"""
                CREATE INDEX rasters_fixture_scene_idx ON {Quote(schema)}.rasters (fixture_id, scene_id);
                CREATE INDEX rasters_extent_gist ON {Quote(schema)}.rasters USING GIST (ST_ConvexHull(rast));
                ANALYZE {Quote(schema)}.rasters;
                """;
            await indexCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private async Task InsertSceneAsync(
        NpgsqlConnection connection,
        string schema,
        RasterStorageLayout layout,
        RasterFixtureDefinition fixture,
        int sceneIndex,
        CancellationToken cancellationToken)
    {
        var scene = fixture.Scenes[sceneIndex];
        await using var command = connection.CreateCommand();
        command.CommandText = layout == RasterStorageLayout.PostgisMonolithicExternal
            ? $"""
                INSERT INTO {Quote(schema)}.rasters (fixture_id, scene_id, rast)
                SELECT @fixture, @scene,
                       ST_AddBand(
                           ST_MakeEmptyRaster(@width, @height, @originX, @originY, @scaleX, @scaleY, @skewX, @skewY, @srid),
                           @pixelType, @initialValue, 0);
                """
            : $"""
                WITH source AS (
                    SELECT ST_AddBand(
                        ST_MakeEmptyRaster(@width, @height, @originX, @originY, @scaleX, @scaleY, @skewX, @skewY, @srid),
                        @pixelType, @initialValue, 0) AS rast
                )
                INSERT INTO {Quote(schema)}.rasters (fixture_id, scene_id, rast)
                SELECT @fixture, @scene, tiled.rast
                FROM source
                CROSS JOIN LATERAL ST_Tile(source.rast, @blockSize, @blockSize, true, 0) AS tiled(rast);
                """;
        command.Parameters.AddWithValue("fixture", fixture.Id);
        command.Parameters.AddWithValue("scene", sceneIndex);
        command.Parameters.AddWithValue("width", scene.Width);
        command.Parameters.AddWithValue("height", scene.Height);
        command.Parameters.AddWithValue("originX", scene.OriginX);
        command.Parameters.AddWithValue("originY", scene.OriginY);
        command.Parameters.AddWithValue("scaleX", scene.ScaleX);
        command.Parameters.AddWithValue("scaleY", scene.ScaleY);
        command.Parameters.AddWithValue("skewX", scene.SkewX);
        command.Parameters.AddWithValue("skewY", scene.SkewY);
        command.Parameters.AddWithValue("srid", scene.Srid);
        command.Parameters.AddWithValue("pixelType", fixture.PixelType);
        command.Parameters.AddWithValue("initialValue", sceneIndex + 1d);
        command.Parameters.AddWithValue("blockSize", options.BlockSize);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RasterStorageWorkloadResult> CreateIngestResultAsync(
        NpgsqlDataSource dataSource,
        string schema,
        RasterStorageLayout layout,
        RasterFixtureDefinition fixture,
        TimeSpan ingest,
        CancellationToken cancellationToken)
    {
        var storage = await ReadStorageBytesAsync(dataSource, schema, cancellationToken).ConfigureAwait(false);
        var samples = new[] { ingest.TotalMilliseconds };
        return RasterStorageStatistics.CreateCompletedResult(
            layout,
            fixture.Id,
            RasterStorageWorkload.Ingest,
            0,
            samples,
            RasterStorageMetrics.ForPostgis(
                default,
                fixture.LogicalBytes,
                storage,
                ingest.TotalMilliseconds));
    }

    private async Task<RasterStorageWorkloadResult> RunWorkloadSafelyAsync(
        NpgsqlDataSource dataSource,
        string schema,
        RasterStorageLayout layout,
        RasterFixtureDefinition fixture,
        RasterStorageWorkload workload,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunWorkloadAsync(dataSource, schema, layout, fixture, workload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return CreateNonCompleted(
                layout,
                fixture.Id,
                workload,
                BenchmarkResultStatus.Failed,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task<RasterStorageWorkloadResult> RunWorkloadAsync(
        NpgsqlDataSource dataSource,
        string schema,
        RasterStorageLayout layout,
        RasterFixtureDefinition fixture,
        RasterStorageWorkload workload,
        CancellationToken cancellationToken)
    {
        var query = BuildWorkloadSql(schema, fixture, workload);
        for (var iteration = 0; iteration < options.WarmupCount; iteration++)
        {
            await ExecuteWorkloadAsync(dataSource, query, workload, cancellationToken).ConfigureAwait(false);
        }

        var before = await ReadDatabaseMetricsAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var samples = new List<double>(options.SampleCount);
        for (var iteration = 0; iteration < options.SampleCount; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            if (workload == RasterStorageWorkload.ConcurrentTenant)
            {
                var tasks = Enumerable.Range(0, options.ConcurrentTenants)
                    .Select(_ => ExecuteWorkloadAsync(dataSource, query, RasterStorageWorkload.Tile, cancellationToken));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            else
            {
                await ExecuteWorkloadAsync(dataSource, query, workload, cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var after = await ReadDatabaseMetricsAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var storage = await ReadStorageBytesAsync(dataSource, schema, cancellationToken).ConfigureAwait(false);
        var delta = new DatabaseMetricDelta(
            Math.Max(0, after.BlocksRead - before.BlocksRead),
            Math.Max(0, after.BlocksHit - before.BlocksHit),
            Math.Max(0, after.TempBytes - before.TempBytes));
        var vacuum = workload == RasterStorageWorkload.Vacuum ? samples.Sum() : (double?)null;
        return RasterStorageStatistics.CreateCompletedResult(
            layout,
            fixture.Id,
            workload,
            options.WarmupCount,
            samples,
            RasterStorageMetrics.ForPostgis(delta, fixture.LogicalBytes, storage, vacuumMilliseconds: vacuum));
    }

    private static async Task ExecuteWorkloadAsync(
        NpgsqlDataSource dataSource,
        string sql,
        RasterStorageWorkload workload,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var enableGdal = connection.CreateCommand())
        {
            // Database-side GDAL is allowed by ADR-0071. Stock PostGIS images disable
            // drivers per session, so enable them only on this isolated benchmark connection.
            enableGdal.CommandText = "SET postgis.gdal_enabled_drivers = 'ENABLE_ALL';";
            await enableGdal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 600;
        if (workload == RasterStorageWorkload.Vacuum)
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildWorkloadSql(
        string schema,
        RasterFixtureDefinition fixture,
        RasterStorageWorkload workload)
    {
        var table = $"{Quote(schema)}.rasters";
        var first = fixture.Scenes[0];
        var centerX = first.OriginX + (first.Width * first.ScaleX / 2);
        var centerY = first.OriginY + (first.Height * first.ScaleY / 2);
        var halfWindow = Math.Abs(first.ScaleX) * 128;
        var envelope = FormattableString.Invariant(
            $"ST_MakeEnvelope({centerX - halfWindow:R}, {centerY - halfWindow:R}, {centerX + halfWindow:R}, {centerY + halfWindow:R}, {first.Srid})");
        var point = FormattableString.Invariant($"ST_SetSRID(ST_Point({centerX:R}, {centerY:R}), {first.Srid})");
        var mosaic = $"SELECT ST_Union(rast) AS rast FROM {table}";

        return workload switch
        {
            RasterStorageWorkload.AlignmentValidation => $"""
                WITH reference AS (
                    SELECT rast FROM {table} WHERE scene_id = 0 ORDER BY tile_id LIMIT 1
                ), candidates AS (
                    SELECT DISTINCT ON (scene_id) scene_id, rast FROM {table} ORDER BY scene_id, tile_id
                )
                SELECT COALESCE(bool_and(ST_SameAlignment(reference.rast, candidates.rast)), true)
                FROM reference CROSS JOIN candidates;
                """,
            RasterStorageWorkload.Tile or RasterStorageWorkload.ConcurrentTenant => $"""
                WITH clipped AS (
                    SELECT ST_Clip(rast, {envelope}, true) AS rast
                    FROM {table}
                    WHERE ST_Intersects(rast, {envelope})
                )
                SELECT octet_length(ST_AsPNG(ST_Resize(ST_Union(rast), 256, 256))) FROM clipped;
                """,
            RasterStorageWorkload.Export => $"SELECT octet_length(ST_AsGDALRaster(rast, 'GTiff')) FROM ({mosaic}) AS source;",
            RasterStorageWorkload.Identify => $"""
                SELECT ST_Value(rast, 1, {point})
                FROM {table}
                WHERE ST_Intersects(rast, {point})
                ORDER BY scene_id, tile_id
                LIMIT 1;
                """,
            RasterStorageWorkload.Statistics => $"SELECT (ST_SummaryStatsAgg(rast, 1, true)).mean FROM {table};",
            RasterStorageWorkload.Mosaic => $"SELECT ST_Width(rast) FROM ({mosaic}) AS source;",
            RasterStorageWorkload.Reproject => $"SELECT ST_Width(ST_Transform(rast, 4326)) FROM ({mosaic}) AS source;",
            RasterStorageWorkload.SurfaceAnalysis => $"""
                SELECT ST_Width(ST_Slope(rast, 1, NULL, '32BF', 'DEGREES', 1.0, false))
                FROM ({mosaic}) AS source;
                """,
            RasterStorageWorkload.ZonalStatistics => $"""
                SELECT (ST_SummaryStats(ST_Clip(rast, {envelope}), 1, true)).mean
                FROM ({mosaic}) AS source;
                """,
            RasterStorageWorkload.Vacuum => $"VACUUM (ANALYZE) {table};",
            _ => throw new InvalidOperationException($"No in-process SQL workload exists for {workload}."),
        };
    }

    private static async Task<DatabaseMetricDelta> ReadDatabaseMetricsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT blks_read, blks_hit, temp_bytes FROM pg_stat_database WHERE datname = current_database();";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("pg_stat_database did not return the current database.");
        }

        return new DatabaseMetricDelta(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> ReadStorageBytesAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_total_relation_size(format('%I.%I', @schema, 'rasters'));";
        command.Parameters.AddWithValue("schema", schema);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(string PostgresVersion, string PostgisVersion)> ReadVersionsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_setting('server_version'), PostGIS_Full_Version();";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL version query returned no rows.");
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task EnsurePostgisRasterAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis_raster;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateScratchSchemaAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA {Quote(schema)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DropScratchSchemaAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {Quote(schema)} CASCADE;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RasterStorageWorkloadResult CreateNonCompleted(
        RasterStorageLayout layout,
        string fixtureId,
        RasterStorageWorkload workload,
        BenchmarkResultStatus status,
        string reason)
        => new(layout, fixtureId, workload, status, 0, [], null, null, [], reason);

    private static bool RequiresAlignedGrid(RasterStorageWorkload workload)
        => workload is RasterStorageWorkload.Tile
            or RasterStorageWorkload.Export
            or RasterStorageWorkload.Mosaic
            or RasterStorageWorkload.Reproject
            or RasterStorageWorkload.SurfaceAnalysis
            or RasterStorageWorkload.ZonalStatistics
            or RasterStorageWorkload.ConcurrentTenant;

    private static string LayoutToken(RasterStorageLayout layout)
        => layout == RasterStorageLayout.PostgisMonolithicExternal ? "mono" : "tile";

    private static string FixtureToken(string fixtureId)
        => fixtureId.Replace("-", string.Empty, StringComparison.Ordinal)[..Math.Min(8, fixtureId.Replace("-", string.Empty, StringComparison.Ordinal).Length)];

    private static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
