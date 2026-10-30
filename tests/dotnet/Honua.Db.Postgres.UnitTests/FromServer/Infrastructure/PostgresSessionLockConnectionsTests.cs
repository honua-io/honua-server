// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Configuration;
using Honua.Db.Postgres;
using Honua.Db.Postgres.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Import and publish paths hold session advisory locks. On Lambda those connections must end
/// their session on close; long-lived hosts keep pooled connections (honua-server#6765).
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class PostgresSessionLockConnectionsTests
{
    private const string Pooled = "Host=localhost;Database=honua;Username=honua;Password=secret;Pooling=true";

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Resolve_LongLivedHost_KeepsPooledConnectionString()
    {
        PostgresSessionLockConnections.Pooled.Resolve(Pooled).Should().Be(Pooled);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Resolve_Lambda_DisablesPoolingAndKeepsDatabaseAndCredentials()
    {
        var locks = new PostgresSessionLockConnections(releaseSessionOnClose: true);

        var built = new NpgsqlConnectionStringBuilder(locks.Resolve(
            "Host=proxy;Database=honua;Username=honua;Password=secret;Pooling=true;Multiplexing=true;Minimum Pool Size=2;Application Name=api"));

        built.Pooling.Should().BeFalse();
        built.Multiplexing.Should().BeFalse();
        built.MinPoolSize.Should().Be(0);
        built.Host.Should().Be("proxy");
        built.Database.Should().Be("honua");
        built.Password.Should().Be("secret");
        built.ApplicationName.Should().Be("api");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void RequiresUnpooled_LambdaAlreadyUnpooled_ReturnsFalse()
    {
        // Lambda named and bound pools are already unpooled (honua-server#6760).
        var locks = new PostgresSessionLockConnections(releaseSessionOnClose: true);

        locks.RequiresUnpooled("Host=localhost;Database=honua;Pooling=false").Should().BeFalse();
        locks.RequiresUnpooled(Pooled).Should().BeTrue();
        PostgresSessionLockConnections.Pooled.RequiresUnpooled(Pooled).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task OpenAsync_LongLivedHost_ReturnsWorkConnectionWithoutOwningIt()
    {
        await using var work = new NpgsqlConnection(Pooled);

        await using var session = await PostgresSessionLockConnections.Pooled.OpenAsync(work, CancellationToken.None);

        session.Connection.Should().BeSameAs(work);
        session.IsUnpooledReplacement.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task OpenAsync_LambdaWithCallerTransaction_KeepsWorkConnection()
    {
        // The work must join the caller's transaction, so the connection is not replaced.
        var locks = new PostgresSessionLockConnections(releaseSessionOnClose: true);
        await using var work = new NpgsqlConnection(Pooled);

        await using var session = await locks.OpenAsync(work, CancellationToken.None, hasCallerTransaction: true);

        session.Connection.Should().BeSameAs(work);
        session.IsUnpooledReplacement.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task OpenAsync_LambdaUnpooledWorkConnection_KeepsWorkConnection()
    {
        var locks = new PostgresSessionLockConnections(releaseSessionOnClose: true);
        await using var work = new NpgsqlConnection("Host=localhost;Database=honua;Pooling=false");

        await using var session = await locks.OpenAsync(work, CancellationToken.None);

        session.Connection.Should().BeSameAs(work);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AddPostgreSqlServices_LongLivedHost_KeepsSessionLocksPooled()
        => ResolveRegistered(lambdaFunctionName: null).ReleaseSessionOnClose.Should().BeFalse();

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AddPostgreSqlServices_Lambda_ReleasesSessionLocksOnClose()
        => ResolveRegistered(lambdaFunctionName: "honua-api").ReleaseSessionOnClose.Should().BeTrue();

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ForPrimaryDataSource_ConnectionMode_ReplaysPoolSessionSettings()
    {
        var limits = new ConnectionLimits { LockTimeout = TimeSpan.FromSeconds(7) };

        var locks = PostgresSessionLockConnections.ForPrimaryDataSource(
            true, limits, schemaHeadersEnabled: false, "honua", DatabaseSessionSettingsMode.Connection);

        locks.SessionSettingsSql.Should().Be(PostgresDataSourceFactory.BuildSessionSettingsSql(limits, "honua"));
        locks.SessionSettingsSql.Should().Contain("lock_timeout");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ForPrimaryDataSource_RoleDefaultsOrStartupOptions_ReplaysNothing()
    {
        var limits = new ConnectionLimits();

        PostgresSessionLockConnections.ForPrimaryDataSource(
                true, limits, schemaHeadersEnabled: false, "honua", DatabaseSessionSettingsMode.RoleDefaults)
            .SessionSettingsSql.Should().BeNull();
        PostgresSessionLockConnections.ForPrimaryDataSource(
                true, limits, schemaHeadersEnabled: true, "honua", DatabaseSessionSettingsMode.Connection)
            .SessionSettingsSql.Should().BeNull();
    }

    private static PostgresSessionLockConnections ResolveRegistered(string? lambdaFunctionName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua_test;Username=honua;Password=test",
                ["AWS_LAMBDA_FUNCTION_NAME"] = lambdaFunctionName,
            })
            .Build();
        var services = new ServiceCollection();

        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PostgresSessionLockConnections>>().Value;
    }
}
