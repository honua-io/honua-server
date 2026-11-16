// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

[Collection("Database")]
public sealed class PostgresDatabaseCompatibilityCheckerPoolTests(PostgresFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;
    private ServiceProvider _services = null!;
    private string _configuredConnectionString = null!;
    private string _databaseName = null!;

    public async Task InitializeAsync()
    {
        var isolated = await fixture.CreateIsolatedDatabaseAsync(nameof(PostgresDatabaseCompatibilityCheckerPoolTests));
        var settings = new NpgsqlConnectionStringBuilder(isolated)
        {
            Pooling = false,
            ApplicationName = $"compatibility-{Guid.NewGuid():N}"
        };
        _databaseName = settings.Database!;
        _configuredConnectionString = settings.ConnectionString;
        // The production data source rewrites the configured string with its effective pool limits.
        settings.Pooling = true;
        settings.MaxPoolSize = 1;
        settings.PersistSecurityInfo = true;
        _dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        _services = CreateServices(_configuredConnectionString, _dataSource);
        await using var command = _dataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS postgis");
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _dataSource.DisposeAsync();
        await fixture.DropDatabaseAsync(_databaseName);
    }

    [IntegrationTest]
    public async Task CheckCompatibilityAsync_WhenConfiguredPoolIsFull_WaitsThenSucceeds()
    {
        var checker = _services.GetRequiredService<IDatabaseCompatibilityChecker>();
        await using (var heldConnection = await _dataSource.OpenConnectionAsync())
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var elapsed = Stopwatch.StartNew();
            var exception = await Record.ExceptionAsync(() => checker.CheckCompatibilityAsync(_configuredConnectionString, cancellation.Token));
            elapsed.Stop();
            output.WriteLine("Configured capacity: 1; cancelled: {0}; elapsed: {1:F1} ms", exception is OperationCanceledException, elapsed.Elapsed.TotalMilliseconds);
            exception.Should().BeAssignableTo<OperationCanceledException>("preflight must wait on the configured pool");
            await using var command = fixture.DataSource.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @database AND application_name = @application");
            command.Parameters.AddWithValue("database", _databaseName);
            command.Parameters.AddWithValue("application", new NpgsqlConnectionStringBuilder(_configuredConnectionString).ApplicationName);
            ((long)(await command.ExecuteScalarAsync())!).Should().Be(1);
        }

        using var available = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await checker.CheckCompatibilityAsync(_configuredConnectionString, available.Token)).IsCompatible.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task CheckCompatibilityAsync_WithExplicitDifferentTarget_PreservesTarget()
    {
        var checker = _services.GetRequiredService<IDatabaseCompatibilityChecker>();
        var settings = new NpgsqlConnectionStringBuilder(_configuredConnectionString)
        {
            Database = $"missing_{Guid.NewGuid():N}"
        };
        await using var heldConnection = await _dataSource.OpenConnectionAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await checker.CheckCompatibilityAsync(settings.ConnectionString, cancellation.Token);

        result.IsCompatible.Should().BeFalse("an explicit missing database must not be redirected to the configured database");
    }

    [IntegrationTest]
    public async Task CheckCompatibilityAsync_WithMalformedConfiguration_DoesNotConstructDataSource()
    {
        const string malformed = "Host=localhost;Password==sensitive;invalid";
        var services = new ServiceCollection();
        services.AddSingleton(new PostgresConnectionStringCache(malformed, null));
        services.AddSingleton<NpgsqlDataSource>(_ => throw new InvalidOperationException("Must not construct the pool"));
        await using var provider = RegisterChecker(services).BuildServiceProvider();

        var result = await provider.GetRequiredService<IDatabaseCompatibilityChecker>().CheckCompatibilityAsync(malformed);

        result.IsCompatible.Should().BeFalse();
        result.ErrorMessage.Should().Be(PostgresDatabaseCompatibilityChecker.MalformedConnectionStringMessage);
    }

    [IntegrationTest]
    public async Task CheckCompatibilityAsync_WithoutServingPool_SupportsMigrationOnlyComposition()
    {
        await using var provider = RegisterChecker(new ServiceCollection()).BuildServiceProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await provider.GetRequiredService<IDatabaseCompatibilityChecker>()
            .CheckCompatibilityAsync(_configuredConnectionString, cancellation.Token);

        result.IsCompatible.Should().BeTrue();
    }

    private static ServiceProvider CreateServices(string configured, NpgsqlDataSource source) =>
        RegisterChecker(new ServiceCollection()
            .AddSingleton(source)
            .AddSingleton(new PostgresConnectionStringCache(configured, null)))
        .BuildServiceProvider();

    private static IServiceCollection RegisterChecker(IServiceCollection services)
    {
        services.AddSingleton<ILogger<PostgresDatabaseCompatibilityChecker>>(NullLogger<PostgresDatabaseCompatibilityChecker>.Instance);
        return services.AddPostgresMigrationServices(new ConfigurationBuilder().Build(),
            new PostgresCoreSchemaMigrationManifest("test", "snapshot", "packages", "raster", "sensors", "overviews", "footprints", "adoption", "lineage"));
    }
}
