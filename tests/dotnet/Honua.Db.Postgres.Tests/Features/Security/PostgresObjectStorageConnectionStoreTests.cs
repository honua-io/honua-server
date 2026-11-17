// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ObjectStorage;
using Honua.Db.Postgres.Features.Security;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Security;

// Two store instances over one database stand in for two server tasks.
[Collection("Database")]
[Protocol(ProtocolNames.TestQuality)]
public sealed class PostgresObjectStorageConnectionStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ConnectionWrittenByOneTask_IsReadByAnotherAndAdvancesTheGeneration()
    {
        await WithSchemaAsync(async schema =>
        {
            var writer = new PostgresObjectStorageConnectionStore(fixture.DataSource, schema);
            var reader = new PostgresObjectStorageConnectionStore(fixture.DataSource, schema);
            var connection = await SetupAsync(writer, "parcels/");
            var before = await GenerationAsync(schema);

            (await writer.CreateAsync(connection, CancellationToken.None)).Should().Be(ObjectStorageCreateOutcome.Created);

            var read = await reader.GetAsync(connection.Id, CancellationToken.None);
            read!.ExternalId.Should().Be(connection.ExternalId);
            read.Prefix.Should().Be("parcels/");
            (await reader.ListAsync(CancellationToken.None)).Should().ContainSingle(item => item.Id == connection.Id);
            (await GenerationAsync(schema)).Should().BeGreaterThan(before);

            (await reader.UpdateAsync(read with { Name = "Renamed" }, CancellationToken.None)).Should().BeTrue();
            (await writer.GetAsync(connection.Id, CancellationToken.None))!.Name.Should().Be("Renamed");
            (await writer.DeleteAsync(connection.Id, CancellationToken.None)).Should().BeTrue();
            (await reader.GetAsync(connection.Id, CancellationToken.None)).Should().BeNull();
        });
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExternalId_IsSingleUseAndMustBeMintedForTheSameBucketAndPrefix()
    {
        await WithSchemaAsync(async schema =>
        {
            var store = new PostgresObjectStorageConnectionStore(fixture.DataSource, schema);
            var connection = await SetupAsync(store, "parcels/");

            (await store.CreateAsync(connection with { ExternalId = ObjectStoragePolicy.NewExternalId() }, CancellationToken.None))
                .Should().Be(ObjectStorageCreateOutcome.SetupRejected, "an unknown external id was never minted");
            (await store.CreateAsync(connection with { Prefix = "other/" }, CancellationToken.None))
                .Should().Be(ObjectStorageCreateOutcome.SetupRejected, "the setup was minted for another prefix");
            (await store.CreateAsync(connection, CancellationToken.None)).Should().Be(ObjectStorageCreateOutcome.Created);
            (await store.FindOpenSetupAsync(connection.ExternalId!, CancellationToken.None)).Should().BeNull();
            (await store.CreateAsync(connection with { Id = Guid.NewGuid() }, CancellationToken.None))
                .Should().Be(ObjectStorageCreateOutcome.SetupRejected, "a consumed external id cannot be reused");
        });
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExpiredExternalId_IsRefused()
    {
        await WithSchemaAsync(async schema =>
        {
            var store = new PostgresObjectStorageConnectionStore(fixture.DataSource, schema);
            var connection = await SetupAsync(store, "parcels/", DateTimeOffset.UtcNow.AddMinutes(-1));

            (await store.FindOpenSetupAsync(connection.ExternalId!, CancellationToken.None)).Should().BeNull();
            (await store.CreateAsync(connection, CancellationToken.None)).Should().Be(ObjectStorageCreateOutcome.SetupRejected);
        });
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExternalIdBucketAndPrefix_AreImmutableInTheDatabase()
    {
        await WithSchemaAsync(async schema =>
        {
            var store = new PostgresObjectStorageConnectionStore(fixture.DataSource, schema);
            var connection = await SetupAsync(store, "parcels/");
            await store.CreateAsync(connection, CancellationToken.None);

            var act = () => fixture.ExecuteAsync(
                $"UPDATE \"{schema}\".object_storage_connections SET external_id = 'replaced' WHERE connection_id = '{connection.Id}'");

            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            (await store.UpdateAsync(connection with { Prefix = "other/" }, CancellationToken.None)).Should().BeFalse();
        });
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task OpenSetups_AreCappedAndExpiredOnesDoNotCount()
    {
        await WithSchemaAsync(async schema =>
        {
            var store = new PostgresObjectStorageConnectionStore(fixture.DataSource, schema);
            await SetupAsync(store, "expired/", DateTimeOffset.UtcNow.AddMinutes(-1));
            for (var i = 0; i < PostgresObjectStorageConnectionStore.MaxOpenSetups; i++)
            {
                (await TrySetupAsync(store)).Should().BeTrue();
            }

            (await TrySetupAsync(store)).Should().BeFalse("the open-setup cap is reached");
        });
    }

    private static Task<bool> TrySetupAsync(PostgresObjectStorageConnectionStore store)
        => store.SaveSetupAsync(
            new ObjectStorageConnectionSetup
            {
                ExternalId = ObjectStoragePolicy.NewExternalId(),
                Bucket = "customer-imports",
                Prefix = "parcels/",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            },
            CancellationToken.None);

    private async Task WithSchemaAsync(Func<string, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresObjectStorageConnectionStoreTests));
        try
        {
            await CoreMigrationTestFixture.ApplyObjectStorageConnectionsAsync(fixture, schema);
            await test(schema);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static async Task<ObjectStorageConnection> SetupAsync(
        PostgresObjectStorageConnectionStore store,
        string prefix,
        DateTimeOffset? expiresAt = null)
    {
        var externalId = ObjectStoragePolicy.NewExternalId();
        await store.SaveSetupAsync(
            new ObjectStorageConnectionSetup
            {
                ExternalId = externalId,
                Bucket = "customer-imports",
                Prefix = prefix,
                ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(24),
            },
            CancellationToken.None);
        return new ObjectStorageConnection
        {
            Id = Guid.NewGuid(),
            Name = "Customer imports",
            Bucket = "customer-imports",
            Prefix = prefix,
            Region = "us-west-2",
            RoleArn = "arn:aws:iam::123456789012:role/honua-connection-imports",
            ExternalId = externalId,
            CreatedBy = "test",
        };
    }

    private async Task<long> GenerationAsync(string schema)
    {
        await using var connection = await fixture.GetConnectionAsync(schema);
        await using var command = new NpgsqlCommand($"SELECT generation FROM \"{schema}\".configuration_generation WHERE singleton", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
