// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Admin;

[Collection("Database")]
public sealed class PostgreSqlTableDiscoveryPoolTests(PostgresFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private NpgsqlDataSource _source = null!;
    private ServiceProvider _services = null!;
    private string _configured = null!;
    private string _database = null!;

    public async Task InitializeAsync()
    {
        var isolated = await fixture.CreateIsolatedDatabaseAsync(nameof(PostgreSqlTableDiscoveryPoolTests));
        var settings = new NpgsqlConnectionStringBuilder(isolated)
        {
            Pooling = false,
            ApplicationName = $"discovery-{Guid.NewGuid():N}"
        };
        _configured = settings.ConnectionString;
        _database = settings.Database!;
        settings.Pooling = true;
        settings.MaxPoolSize = 1;
        _source = NpgsqlDataSource.Create(settings.ConnectionString);
        var services = new ServiceCollection().AddLogging().AddSingleton(_source);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _configured
        }).Build();
        services.AddPostgreSqlServices(configuration, new PostgresCoreSchemaMigrationManifest(
            "test", "snapshot", "packages", "raster", "sensors", "overviews", "footprints", "adoption", "lineage"));
        _services = services.BuildServiceProvider();
        await using var command = _source.CreateCommand("CREATE EXTENSION IF NOT EXISTS postgis; CREATE TABLE public.points (id integer PRIMARY KEY, geom geometry(Point,4326))");
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _source.DisposeAsync();
        await fixture.DropDatabaseAsync(_database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_WhenConfiguredPoolIsFull_WaitsThenSucceeds(bool singleTable)
    {
        await using var scope = _services.CreateAsyncScope();
        var service = (PostgreSqlTableDiscoveryService)scope.ServiceProvider.GetRequiredService<ITableDiscoveryService>();
        await using (var held = await _source.OpenConnectionAsync())
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var elapsed = Stopwatch.StartNew();
            var exception = await Record.ExceptionAsync(() => DiscoverAsync(service, _configured, singleTable, cancellation.Token));
            elapsed.Stop();
            output.WriteLine("Single table: {0}; capacity: 1; cancelled: {1}; elapsed: {2:F1} ms", singleTable, exception is OperationCanceledException, elapsed.Elapsed.TotalMilliseconds);
            exception.Should().BeAssignableTo<OperationCanceledException>("discovery must borrow from the configured pool");
            await using var command = fixture.DataSource.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @database AND application_name = @application");
            command.Parameters.AddWithValue("database", _database);
            command.Parameters.AddWithValue("application", new NpgsqlConnectionStringBuilder(_configured).ApplicationName);
            ((long)(await command.ExecuteScalarAsync())!).Should().Be(1);
        }

        using var available = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await DiscoverAsync(service, _configured, singleTable, available.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_WithExplicitDifferentTarget_PreservesTarget(bool singleTable)
    {
        await using var scope = _services.CreateAsyncScope();
        var service = (PostgreSqlTableDiscoveryService)scope.ServiceProvider.GetRequiredService<ITableDiscoveryService>();
        var alternate = new NpgsqlConnectionStringBuilder(_configured) { Database = $"missing_{Guid.NewGuid():N}" };
        await using var held = await _source.OpenConnectionAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var exception = await Record.ExceptionAsync(() => DiscoverAsync(service, alternate.ConnectionString, singleTable, cancellation.Token));

        exception.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("3D000");
    }

    private static async Task DiscoverAsync(PostgreSqlTableDiscoveryService service, string target, bool singleTable, CancellationToken cancellationToken)
    {
        if (singleTable)
        {
            (await service.DiscoverPostGisTableAsync(target, "public", "points", cancellationToken)).Should().NotBeNull();
        }
        else
        {
            (await service.DiscoverPostGisTablesAsync(target, cancellationToken)).Should().Contain(table => table.Table == "points");
        }
    }
}
