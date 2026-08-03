// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster.Functions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Database")]
public sealed class PostgresRasterFunctionDefinitionStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Store_VersionsAreImmutableIdempotentAndFailClosed()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterFunctionDefinitionStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var store = CreateStore(schema);
            var firstRequest = CreateRequest("tenant-a", "request-1", 0, CreateDefinition());

            var created = await store.CreateVersionAsync(firstRequest);
            var replayed = await store.CreateVersionAsync(firstRequest);
            var idempotencyConflict = await store.CreateVersionAsync(firstRequest with
            {
                Definition = CreateDefinition(includeIdentity: true),
            });
            var versionConflict = await store.CreateVersionAsync(firstRequest with
            {
                IdempotencyKey = "request-stale",
            });

            created.Status.Should().Be(RasterFunctionDefinitionCreateStatus.Created);
            created.DefinitionVersion!.Version.Should().Be(1);
            replayed.Status.Should().Be(RasterFunctionDefinitionCreateStatus.Replayed);
            replayed.DefinitionVersion.Should().BeEquivalentTo(created.DefinitionVersion);
            idempotencyConflict.Status.Should().Be(RasterFunctionDefinitionCreateStatus.IdempotencyConflict);
            idempotencyConflict.DefinitionVersion.Should().BeNull();
            versionConflict.Status.Should().Be(RasterFunctionDefinitionCreateStatus.VersionConflict);
            versionConflict.CurrentVersion.Should().Be(1);

            var second = await store.CreateVersionAsync(CreateRequest(
                "tenant-a",
                "request-2",
                1,
                CreateDefinition(includeIdentity: true)));
            second.Status.Should().Be(RasterFunctionDefinitionCreateStatus.Created);
            second.DefinitionVersion!.Version.Should().Be(2);

            var exact = await store.GetVersionAsync(ToReference(second.DefinitionVersion));
            var wrongTenant = await store.GetVersionAsync(ToReference(second.DefinitionVersion) with { TenantId = "tenant-b" });
            var wrongName = await store.GetVersionAsync(ToReference(second.DefinitionVersion) with { Name = "other-function" });
            var wrongVersion = await store.GetVersionAsync(ToReference(second.DefinitionVersion) with { Version = 1 });
            var wrongHash = await store.GetVersionAsync(ToReference(second.DefinitionVersion) with { DefinitionHash = new string('0', 64) });

            exact.Should().BeEquivalentTo(second.DefinitionVersion);
            wrongTenant.Should().BeNull();
            wrongName.Should().BeNull();
            wrongVersion.Should().BeNull();
            wrongHash.Should().BeNull();

            var update = async () => await ExecuteAsync(
                schema,
                $"UPDATE \"{schema}\".raster_function_definition_versions SET created_by = 'tampered' WHERE version = 1;");
            var delete = async () => await ExecuteAsync(
                schema,
                $"DELETE FROM \"{schema}\".raster_function_definition_versions WHERE version = 1;");

            (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55000");
            (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55000");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Store_ConcurrentCreatesSerializePerTenantAndName()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterFunctionDefinitionStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var store = CreateStore(schema);
            var sameRequest = CreateRequest("tenant-a", "same-request", 0, CreateDefinition());

            var sameKeyResults = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => store.CreateVersionAsync(sameRequest)));

            sameKeyResults.Should().ContainSingle(result => result.Status == RasterFunctionDefinitionCreateStatus.Created);
            sameKeyResults.Should().HaveCount(8);
            sameKeyResults.Should().OnlyContain(result =>
                result.Status == RasterFunctionDefinitionCreateStatus.Created
                || result.Status == RasterFunctionDefinitionCreateStatus.Replayed);
            sameKeyResults.Select(result => result.DefinitionVersion!.Version).Should().OnlyContain(version => version == 1);

            var distinctKeyResults = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(index => store.CreateVersionAsync(
                    CreateRequest("tenant-a", $"next-{index}", 1, CreateDefinition(includeIdentity: true)))));

            distinctKeyResults.Should().ContainSingle(result => result.Status == RasterFunctionDefinitionCreateStatus.Created);
            distinctKeyResults.Should().HaveCount(8);
            distinctKeyResults.Should().OnlyContain(result =>
                result.Status == RasterFunctionDefinitionCreateStatus.Created
                || result.Status == RasterFunctionDefinitionCreateStatus.VersionConflict);
            distinctKeyResults.Max(result => result.CurrentVersion).Should().Be(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private PostgresRasterFunctionDefinitionStore CreateStore(string schema)
        => new(new TestConnectionProvider(fixture.DataSource, schema), schema);

    private async Task EnsureTablesAsync(string schema)
    {
        var repositoryRoot = FindRepoRoot();
        var migration = await File.ReadAllTextAsync(Path.Join(
            repositoryRoot,
            "src",
            "Honua.Server",
            "Migrations",
            "092_CreateRasterFunctionDefinitions.sql"));
        var isolatedMigration = migration.Replace("honua.", $"\"{schema}\".", StringComparison.Ordinal);
        await ExecuteAsync(schema, isolatedMigration);
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO \"{schema}\", public;\n{sql}";
        await command.ExecuteNonQueryAsync();
    }

    private static RasterFunctionDefinitionCreateRequest CreateRequest(
        string tenantId,
        string idempotencyKey,
        int expectedVersion,
        RasterFunctionDefinition definition)
        => new()
        {
            TenantId = tenantId,
            Name = "vegetation",
            Definition = definition,
            ExpectedLatestVersion = expectedVersion,
            IdempotencyKey = idempotencyKey,
            CreatedBy = "test-user",
        };

    private static RasterFunctionDefinition CreateDefinition(bool includeIdentity = false)
        => includeIdentity
            ? new RasterFunctionDefinition
            {
                Nodes =
                [
                    new RasterFunctionInputNode { Id = "source", InputName = "imagery" },
                    new RasterFunctionIdentityNode { Id = "output", Inputs = ["source"] },
                ],
                OutputNodeId = "output",
            }
            : new RasterFunctionDefinition
            {
                Nodes = [new RasterFunctionInputNode { Id = "source", InputName = "imagery" }],
                OutputNodeId = "source",
            };

    private static RasterFunctionDefinitionReference ToReference(RasterFunctionDefinitionVersion version)
        => new()
        {
            TenantId = version.TenantId,
            Name = version.Name,
            Version = version.Version,
            DefinitionHash = version.DefinitionHash,
        };

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(
                current.FullName,
                "src",
                "Honua.Server",
                "Migrations",
                "092_CreateRasterFunctionDefinitions.sql")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be located.");
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
