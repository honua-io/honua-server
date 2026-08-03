// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Database")]
public sealed class PostgisRasterDatabaseConnectionProviderIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task OpenConnectionAsync_AppliesRoleTenantAndTimeoutFenceBeforeReturning()
    {
        var options = Options();
        await using var dataSource = PostgisRasterDataSource.Create(fixture.ConnectionString, options);
        var provider = new PostgisRasterDatabaseConnectionProvider(
            dataSource,
            options,
            "tenant-a",
            "operation-123",
            3,
            "public");

        await using var connection = await provider.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current_user,
                   current_setting('honua.tenant_id'),
                   current_setting('honua.operation_id'),
                   current_setting('honua.attempt'),
                   current_setting('statement_timeout'),
                   current_setting('lock_timeout'),
                   current_setting('idle_in_transaction_session_timeout');
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetString(0).Should().Be(options.RequiredRole);
        reader.GetString(1).Should().Be("tenant-a");
        reader.GetString(2).Should().Be("operation-123");
        reader.GetString(3).Should().Be("3");
        reader.GetString(4).Should().Be("2s");
        reader.GetString(5).Should().Be("250ms");
        reader.GetString(6).Should().Be("3s");
    }

    [Fact]
    public async Task OpenConnectionAsync_WrongDatabaseRole_FailsClosed()
    {
        var options = Options();
        options.RequiredRole = "not_the_fixture_role";
        await using var dataSource = PostgisRasterDataSource.Create(fixture.ConnectionString, options);
        var provider = new PostgisRasterDatabaseConnectionProvider(
            dataSource,
            options,
            "tenant-a",
            "operation-123",
            1,
            "public");

        var act = async () => await provider.OpenConnectionAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<PostgisRasterGovernanceException>();
        exception.Which.ErrorCode.Should().Be("postgis-raster-role-mismatch");
    }

    [Fact]
    public async Task GovernedCommand_CancellationActivelyInterruptsNpgsqlCommand()
    {
        var options = Options();
        options.StatementTimeout = TimeSpan.FromSeconds(30);
        await using var dataSource = PostgisRasterDataSource.Create(fixture.ConnectionString, options);
        var provider = new PostgisRasterDatabaseConnectionProvider(
            dataSource,
            options,
            "tenant-a",
            "operation-cancel",
            1,
            "public");
        await using var connection = await provider.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_sleep(30);";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var act = async () => await command.ExecuteNonQueryAsync(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private PostgisRasterExecutionOptions Options()
    {
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        return new PostgisRasterExecutionOptions
        {
            RequiredRole = connection.Username ??
                throw new InvalidOperationException("The PostgreSQL fixture must declare a username."),
            SearchPathSchema = "public",
            MaxConcurrency = 2,
            MaxConcurrencyPerTenant = 1,
            QueueTimeout = TimeSpan.FromSeconds(1),
            StatementTimeout = TimeSpan.FromSeconds(2),
            LockTimeout = TimeSpan.FromMilliseconds(250),
            IdleInTransactionTimeout = TimeSpan.FromSeconds(3),
        };
    }
}
