// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace Honua.Server.Tests;

[Collection("Database.CoreFeatureStore")]
[Protocol(ProtocolNames.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresCoreSchemaGuardPoolTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private NpgsqlDataSource _source = null!;
    private ServiceProvider _services = null!;
    private string _connectionString = null!;
    private string _databaseName = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        var isolated = await _postgres.CreateIsolatedDatabaseAsync(nameof(PostgresCoreSchemaGuardPoolTests));
        var settings = new NpgsqlConnectionStringBuilder(isolated)
        {
            Pooling = false,
            ApplicationName = $"schema-guard-{Guid.NewGuid():N}"
        };
        _databaseName = settings.Database!;
        _connectionString = settings.ConnectionString;
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest), ServerCoreSchemaMigrations.Manifest);
        var result = await runner.RunMigrationsAsync(_connectionString, typeof(Program).Assembly);
        result.Successful.Should().BeTrue($"the real schema floor must be installed: {result.ErrorMessage}");
        settings.Pooling = true;
        settings.MaxPoolSize = 1;
        settings.PersistSecurityInfo = true;
        _source = NpgsqlDataSource.Create(settings.ConnectionString);
        _services = RegisterGuard(new ServiceCollection()
            .AddSingleton(_source)
            .AddSingleton(new PostgresConnectionStringCache(_connectionString, null)))
            .BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }
        if (_databaseName is not null)
        {
            await _postgres.DropDatabaseAsync(_databaseName);
        }
        await _postgres.DisposeAsync();
    }

    [IntegrationTest]
    public async Task VerifyAsync_WhenConfiguredPoolIsFull_WaitsThenVerifiesRealSchema()
    {
        var guard = _services.GetRequiredService<IDatabaseSchemaGuard>();
        await using (var held = await _source.OpenConnectionAsync())
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var elapsed = Stopwatch.StartNew();
            var exception = await Record.ExceptionAsync(() => guard.VerifyAsync(_connectionString, cancellation.Token));
            elapsed.Stop();
            output.WriteLine("Capacity: 1; cancelled: {0}; elapsed: {1:F1} ms", exception is OperationCanceledException, elapsed.Elapsed.TotalMilliseconds);
            exception.Should().BeAssignableTo<OperationCanceledException>("schema verification must wait on the configured pool");
            await using var count = _postgres.DataSource.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = @database AND application_name = @application");
            count.Parameters.AddWithValue("database", _databaseName);
            count.Parameters.AddWithValue("application", new NpgsqlConnectionStringBuilder(_connectionString).ApplicationName!);
            ((long)(await count.ExecuteScalarAsync())!).Should().Be(1);
        }

        using var available = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await guard.VerifyAsync(_connectionString, available.Token);
    }

    [IntegrationTest]
    public async Task VerifyAsync_WithCallerOwnedConnection_DoesNotBorrowOrDispose()
    {
        var guard = _services.GetRequiredService<IDatabaseSchemaGuard>();
        await using var held = await _source.OpenConnectionAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await guard.VerifyAsync(held, cancellation.Token);

        await using var command = new NpgsqlCommand("SELECT 1", held);
        (await command.ExecuteScalarAsync(cancellation.Token)).Should().Be(1);
    }

    [IntegrationTest]
    public async Task VerifyAsync_WithExplicitDifferentTarget_PreservesTarget()
    {
        var guard = _services.GetRequiredService<IDatabaseSchemaGuard>();
        var settings = new NpgsqlConnectionStringBuilder(_connectionString) { Database = $"missing_{Guid.NewGuid():N}" };
        await using var held = await _source.OpenConnectionAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var exception = await Record.ExceptionAsync(() => guard.VerifyAsync(settings.ConnectionString, cancellation.Token));

        exception.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.InvalidCatalogName);
    }

    [IntegrationTest]
    public async Task VerifyAsync_WithoutServingPool_SupportsMigrationOnlyComposition()
    {
        await using var provider = RegisterGuard(new ServiceCollection()).BuildServiceProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await provider.GetRequiredService<IDatabaseSchemaGuard>().VerifyAsync(_connectionString, cancellation.Token);
    }

    private static IServiceCollection RegisterGuard(IServiceCollection services) =>
        services.AddPostgresMigrationServices(new ConfigurationBuilder().Build(), ServerCoreSchemaMigrations.Manifest);
}
