// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Admin;

[Collection("Database")]
public sealed class LayerPublicationBudgetTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Constructor_RejectsUnboundedOrInvalidBudgets(int seconds)
    {
        var create = () => CreateService(seconds);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SnapshotCommand_UsesWorkloadBudgetBeyondOrdinaryCommandTimeout()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT 7 FROM pg_sleep(1.2)", connection)
        {
            CommandTimeout = 1
        };

        var result = await CreateService(5).ExecuteSnapshotCommandAsync(command, CancellationToken.None);

        result.Should().Be(7);
        command.CommandTimeout.Should().Be(5 + PostgreSqlLayerPublishingService.SnapshotCommandTimeoutGraceSeconds);
    }

    [Fact]
    public async Task SnapshotBudgetExpiry_AllowsTransactionRollbackAndRetainsSource()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var setup = new NpgsqlCommand(
            "CREATE TEMP TABLE publication_budget_source(id int); INSERT INTO publication_budget_source VALUES (7); "
            + "CREATE TEMP TABLE publication_budget_target(id int)", connection);
        await setup.ExecuteNonQueryAsync();

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO publication_budget_target SELECT * FROM publication_budget_source; SELECT 1 FROM pg_sleep(10)",
                connection, transaction);
            var execute = () => CreateService(1).ExecuteSnapshotCommandAsync(command, CancellationToken.None);
            await execute.Should().ThrowAsync<TimeoutException>()
                .WithMessage("*1-second budget*source table is retained*");
            await transaction.RollbackAsync();
        }

        await using var verify = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM publication_budget_source), (SELECT count(*) FROM publication_budget_target)",
            connection);
        await using var reader = await verify.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.GetInt64(1).Should().Be(0);
    }

    [Fact]
    public async Task SnapshotCommand_PropagatesCallerCancellation()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT 1 FROM pg_sleep(10)", connection);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var execute = () => CreateService(5).ExecuteSnapshotCommandAsync(command, cancellation.Token);

        await execute.Should().ThrowAsync<OperationCanceledException>();
    }

    private static PostgreSqlLayerPublishingService CreateService(int seconds)
        => new(Mock.Of<ITableDiscoveryService>(), Mock.Of<IMetadataV2GraphStore>(),
            NullLogger<PostgreSqlLayerPublishingService>.Instance,
            publishingOptions: new LayerPublishingOptions { MaterializationTimeoutSeconds = seconds });
}
