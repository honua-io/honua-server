// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Import;

[Collection("Database")]
public sealed class PostgresMigrationCatalogWriterPoolTests(PostgresFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;
    private ServiceProvider _services = null!;
    private string _databaseName = null!;
    private string _applicationName = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync(nameof(PostgresMigrationCatalogWriterPoolTests));
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 1,
            PersistSecurityInfo = true,
            ApplicationName = $"catalog-writer-{Guid.NewGuid():N}"
        };
        _databaseName = settings.Database!;
        _applicationName = settings.ApplicationName!;
        _dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        _services = new ServiceCollection()
            .AddSingleton(_dataSource)
            .AddSingleton<ILogger<PostgresMigrationCatalogWriter>>(NullLogger<PostgresMigrationCatalogWriter>.Instance)
            .AddScoped<IMigrationCatalogWriter, PostgresMigrationCatalogWriter>()
            .BuildServiceProvider();

        var migration = await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Migrations", "001_CreateHonuaSchema.sql"));
        await using var command = _dataSource.CreateCommand(migration);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _dataSource.DisposeAsync();
        await fixture.DropDatabaseAsync(_databaseName);
    }

    [IntegrationTest]
    public async Task EnsureCatalogServiceAsync_WhenConfiguredPoolIsFull_WaitsThenWritesIdempotently()
    {
        using var scope = _services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IMigrationCatalogWriter>();
        var request = NewRequest();

        await using (var heldConnection = await _dataSource.OpenConnectionAsync())
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var elapsed = Stopwatch.StartNew();
            var exception = await Record.ExceptionAsync(async () =>
                await writer.EnsureCatalogServiceAsync(_dataSource.ConnectionString, request, cancellation.Token));

            elapsed.Stop();
            var sessions = await CountSessionsAsync();
            output.WriteLine("Configured capacity: 1; database sessions: {0}; elapsed: {1:F1} ms", sessions, elapsed.Elapsed.TotalMilliseconds);
            exception.Should().BeAssignableTo<OperationCanceledException>();
            sessions.Should().Be(1, "the writer must wait on the configured pool instead of opening another session");
        }

        using var available = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await writer.EnsureCatalogServiceAsync(_dataSource.ConnectionString, request, available.Token))
            .Should().Be(MigrationCatalogWriteOutcome.Created);
        (await writer.EnsureCatalogServiceAsync(_dataSource.ConnectionString, request, available.Token))
            .Should().Be(MigrationCatalogWriteOutcome.AlreadyExists);
    }

    [IntegrationTest]
    public async Task EnsureCatalogServiceAsync_WithDifferentConnectionString_PreservesExplicitTarget()
    {
        using var scope = _services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IMigrationCatalogWriter>();
        var settings = new NpgsqlConnectionStringBuilder(_dataSource.ConnectionString)
        {
            ApplicationName = $"explicit-target-{Guid.NewGuid():N}",
            // No global pool keeps an idle session on the isolated database after the test.
            Pooling = false
        };
        await using var heldConnection = await _dataSource.OpenConnectionAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await writer.EnsureCatalogServiceAsync(settings.ConnectionString, NewRequest(), cancellation.Token);

        result.Should().Be(MigrationCatalogWriteOutcome.Created);
    }

    private async Task<long> CountSessionsAsync()
    {
        await using var command = fixture.DataSource.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @database AND application_name = @application");
        command.Parameters.AddWithValue("database", _databaseName);
        command.Parameters.AddWithValue("application", _applicationName);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static MigrationCatalogServiceRequest NewRequest() => new()
    {
        ServiceName = $"pool-probe-{Guid.NewGuid():N}",
        Description = "Pool capacity probe"
    };
}
