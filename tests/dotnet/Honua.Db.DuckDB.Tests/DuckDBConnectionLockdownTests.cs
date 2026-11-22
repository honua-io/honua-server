// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DuckDB.NET.Data;
using Honua.Db.DuckDB.Features.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.DuckDB.Tests;

public sealed class DuckDBConnectionLockdownTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"honua_lockdown_{Guid.NewGuid():N}");

    public DuckDBConnectionLockdownTests()
    {
        Directory.CreateDirectory(Path.Join(_root, "parcels"));
        Directory.CreateDirectory(Path.Join(_root, "private"));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void BuildCommands_AllowsGlobPrefixesAndExactFiles()
    {
        var commands = DuckDBConnectionLockdown.BuildCommands(
            ["s3://county/parcels/year=*/*.parquet", "/data/roads.parquet", "az://lake/zones/", "file:///data/rivers/*.parquet"],
            "/var/tmp/honua.tmp")
            .Select(command => command.Sql)
            .ToList();

        Assert.Equal(
            [
                "SET autoinstall_known_extensions = false",
                "SET autoload_known_extensions = false",
                "SET allowed_directories = ['/data/rivers/', '/var/tmp/honua.tmp/', 'az://lake/zones/', 'file:///data/rivers/', 's3://county/parcels/']",
                "SET allowed_paths = ['/data/roads.parquet']",
                "SET enable_external_access = false",
                "SET lock_configuration = true",
            ],
            commands);
    }

    [Fact]
    public void BuildCommands_WithRelativeGlob_AllowsTheResolvedDirectory()
    {
        var commands = DuckDBConnectionLockdown.BuildCommands(["data/*.parquet"], tempDirectory: null)
            .Select(command => command.Sql)
            .ToList();

        var expected = Path.GetFullPath("data").TrimEnd('/') + "/";
        Assert.Contains(commands, sql => sql.StartsWith("SET allowed_directories = ", StringComparison.Ordinal) &&
            sql.Contains("'data/'", StringComparison.Ordinal) &&
            sql.Contains(DuckDBExternalSourceSql.QuoteLiteral(expected), StringComparison.Ordinal));
        Assert.DoesNotContain(commands, sql => sql.StartsWith("SET allowed_paths", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bootstrap_ConfiguredSourceStaysReadable_OtherFilesAndSettingsAreRefused()
    {
        var allowed = Path.Join(_root, "parcels", "a.parquet");
        var outside = Path.Join(_root, "private", "b.parquet");
        await WriteParquetAsync(allowed);
        await WriteParquetAsync(outside);
        using var bootstrap = CreateBootstrap(Path.Join(_root, "parcels", "*.parquet"));

        await using var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await bootstrap.EnsureSpatialExtensionAsync(connection, CancellationToken.None);

        Assert.Equal(3L, await ScalarAsync(connection, "SELECT count(*) FROM \"parcels\""));
        Assert.Equal("POINT (1 2)", await ScalarAsync(connection, "SELECT ST_AsText(ST_Point(1, 2))"));
        await AssertRefusedAsync(connection, $"SELECT count(*) FROM read_parquet({DuckDBExternalSourceSql.QuoteLiteral(outside)})");
        await AssertRefusedAsync(connection, "SELECT * FROM read_csv('/etc/hostname')");
        await AssertRefusedAsync(connection, $"COPY (SELECT 1) TO {DuckDBExternalSourceSql.QuoteLiteral(Path.Join(_root, "private", "out.csv"))}");
        await AssertRefusedAsync(connection, $"ATTACH {DuckDBExternalSourceSql.QuoteLiteral(Path.Join(_root, "private", "other.duckdb"))}");
        await AssertRefusedAsync(connection, "SET enable_external_access = true");
        await AssertRefusedAsync(connection, "RESET lock_configuration");
        await AssertRefusedAsync(connection, "INSTALL json");
    }

    [Fact]
    public async Task Bootstrap_SharedFileDatabase_LaterConnectionsKeepSettingsAndSources()
    {
        await WriteParquetAsync(Path.Join(_root, "parcels", "a.parquet"));
        var options = new DuckDBOptions
        {
            HttpFs = new DuckDBHttpFsOptions { S3 = new DuckDBS3Options { Region = "eu-north-1" } },
            Layers = [ParcelsLayer(Path.Join(_root, "parcels", "*.parquet"))],
        };
        using var bootstrap = new DuckDBSpatialBootstrap(
            extensionPath: null,
            additionalExtensions: DuckDBExternalSourceSql.GetRequiredExtensions(options),
            connectionSettingCommands: DuckDBExternalSourceSql.BuildConnectionSettingCommands(options),
            externalSourcePlans: DuckDBExternalSourceSql.BuildExternalSourcePlans(options.Layers),
            logger: NullLogger<DuckDBSpatialBootstrap>.Instance);
        var connectionString = $"Data Source={Path.Join(_root, "shared.duckdb")}";

        await using var first = new DuckDBConnection(connectionString);
        await using var second = new DuckDBConnection(connectionString);
        await using var third = new DuckDBConnection(connectionString);
        await Task.WhenAll(first.OpenAsync(), second.OpenAsync(), third.OpenAsync());
        await Task.WhenAll(
            bootstrap.EnsureSpatialExtensionAsync(first, CancellationToken.None),
            bootstrap.EnsureSpatialExtensionAsync(second, CancellationToken.None),
            bootstrap.EnsureSpatialExtensionAsync(third, CancellationToken.None));

        foreach (var connection in new[] { first, second, third })
        {
            Assert.True(await DuckDBConnectionLockdown.IsLockedAsync(connection, CancellationToken.None));
            Assert.Equal("eu-north-1", await ScalarAsync(connection, "SELECT current_setting('s3_region')"));
            Assert.Equal(3L, await ScalarAsync(connection, "SELECT count(*) FROM \"parcels\""));
            Assert.Equal("POINT (1 2)", await ScalarAsync(connection, "SELECT ST_AsText(ST_Point(1, 2))"));
        }
    }

    [Fact]
    public async Task Bootstrap_ReadWriteFileDatabase_StillWritesCheckpointsAndSpills()
    {
        await WriteParquetAsync(Path.Join(_root, "parcels", "a.parquet"));
        using var bootstrap = CreateBootstrap(Path.Join(_root, "parcels", "*.parquet"));

        await using var connection = new DuckDBConnection($"Data Source={Path.Join(_root, "private", "rw.duckdb")}");
        await connection.OpenAsync();
        await LimitMemoryAsync(connection);
        await bootstrap.EnsureSpatialExtensionAsync(connection, CancellationToken.None);

        await ScalarAsync(connection, "CREATE TABLE notes AS SELECT range AS id FROM range(1000)");
        await ScalarAsync(connection, "CHECKPOINT");
        Assert.Equal(1000L, await ScalarAsync(connection, "SELECT count(*) FROM notes"));

        // The sort does not fit in 64 MB: without a temp directory it fails, so success
        // under the lockdown shows spilling to the temp directory still works.
        Assert.Equal(20_000_000L, await ScalarAsync(connection, SpillingSortSql));
        await using var noSpill = new DuckDBConnection("Data Source=:memory:");
        await noSpill.OpenAsync();
        await LimitMemoryAsync(noSpill);
        await ScalarAsync(noSpill, "SET temp_directory = ''");
        await AssertRefusedAsync(noSpill, SpillingSortSql);
    }

    private const string SpillingSortSql =
        "SELECT count(*) FROM (SELECT range AS id, random() AS r FROM range(20000000) ORDER BY r)";

    private static async Task LimitMemoryAsync(DuckDBConnection connection)
    {
        await ScalarAsync(connection, "SET memory_limit = '64MB'");
        await ScalarAsync(connection, "SET threads = 2");
        await ScalarAsync(connection, "SET preserve_insertion_order = false");
    }

    private static DuckDBLayerOptions ParcelsLayer(string sourcePath)
        => new()
        {
            Id = 1,
            Table = "parcels",
            ExternalSource = new DuckDBExternalSourceOptions { Path = sourcePath }
        };

    private static DuckDBSpatialBootstrap CreateBootstrap(string sourcePath)
        => new(
            extensionPath: null,
            additionalExtensions: [],
            connectionSettingCommands: [],
            externalSourcePlans: DuckDBExternalSourceSql.BuildExternalSourcePlans([ParcelsLayer(sourcePath)]),
            logger: NullLogger<DuckDBSpatialBootstrap>.Instance);

    private static async Task WriteParquetAsync(string path)
    {
        await using var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"COPY (SELECT range AS id FROM range(3)) TO {DuckDBExternalSourceSql.QuoteLiteral(path)} (FORMAT parquet)";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(DuckDBConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task AssertRefusedAsync(DuckDBConnection connection, string sql)
    {
        await Assert.ThrowsAnyAsync<DuckDBException>(() => ScalarAsync(connection, sql));
    }
}
