// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Db.Postgres.Features.WorkflowPackages;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.WorkflowPackages;

/// <summary>
/// Drives <see cref="PostgresWorkflowPackageStore"/> against PostGIS. A second store
/// instance is a peer; a third, created after the writer is dropped, is a restart.
/// </summary>
[Collection("Database")]
public sealed class PostgresWorkflowPackageStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task PackageVersionAndPublication_SurviveRestart_AndAreVisibleToAPeer()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresWorkflowPackageStoreTests));
        try
        {
            await ApplyMigrationAsync(schema);
            var writer = new PostgresWorkflowPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var package = await writer.SavePackageAsync(SamplePackage("pkg-restart"));
            var hash = WorkflowPackageGraphHasher.Compute(package.Graph);
            var version = await writer.CreateVersionAsync(
                package.PackageId,
                hash,
                package.Graph,
                WorkflowPackageValidationResult.Success(hash),
                "author");
            var publication = await writer.SavePublicationAsync(SamplePublication(package.PackageId, version.Version, "pub-restart"));

            var peer = new PostgresWorkflowPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            (await peer.GetPackageAsync(package.PackageId))!.Name.Should().Be("pkg-restart");
            (await peer.GetVersionAsync(package.PackageId, version.Version))!.PackageHash.Should().Be(hash);
            (await peer.GetPublicationAsync(publication.PublicationId))!.WorkflowDefinitionId.Should().Be("workflow-package:pkg-restart:v1");
            (await peer.ListPublicationsAsync(package.PackageId)).Should().ContainSingle(item => item.PublicationId == publication.PublicationId);

            var restarted = new PostgresWorkflowPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var afterRestart = await restarted.GetPublicationAsync(publication.PublicationId);
            afterRestart.Should().NotBeNull();
            afterRestart!.Status.Should().Be(WorkflowPublicationStatus.Active);
            (await restarted.GetPackageAsync(package.PackageId))!.LatestVersion.Should().Be(1);

            var disabled = await restarted.SetPublicationStatusAsync(publication.PublicationId, WorkflowPublicationStatus.Disabled);
            disabled!.Status.Should().Be(WorkflowPublicationStatus.Disabled);
            (await peer.GetPublicationAsync(publication.PublicationId))!.Status.Should().Be(WorkflowPublicationStatus.Disabled);

            (await restarted.DeletePublicationAsync(publication.PublicationId))!.PublicationId.Should().Be(publication.PublicationId);
            (await peer.GetPublicationAsync(publication.PublicationId)).Should().BeNull();
            (await restarted.GetVersionAsync(package.PackageId, 1)).Should().NotBeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task CreateVersion_WhenDraftChangedAfterValidation_RejectsTheVersion()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresWorkflowPackageStoreTests) + "Conflict");
        try
        {
            await ApplyMigrationAsync(schema);
            var writer = new PostgresWorkflowPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var package = await writer.SavePackageAsync(SamplePackage("pkg-conflict"));
            var validated = package.Graph;
            var hash = WorkflowPackageGraphHasher.Compute(validated);
            var drifted = validated with
            {
                Nodes =
                [
                    validated.Nodes[0] with
                    {
                        Parameters = new Dictionary<string, string> { ["wkb"] = "changed" }
                    }
                ]
            };
            await writer.SavePackageAsync(package with { Graph = drifted });

            var act = async () => await writer.CreateVersionAsync(
                package.PackageId,
                hash,
                validated,
                WorkflowPackageValidationResult.Success(hash),
                "author");

            await act.Should().ThrowAsync<WorkflowPackageGraphConflictException>();
            (await writer.ListVersionsAsync(package.PackageId)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task CreateVersion_StoresValidatedGraph_WhenUnhashedScheduleDrifts()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresWorkflowPackageStoreTests) + "Snapshot");
        try
        {
            await ApplyMigrationAsync(schema);
            var writer = new PostgresWorkflowPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var package = await writer.SavePackageAsync(SamplePackage("pkg-snapshot"));
            var validated = package.Graph;
            var hash = WorkflowPackageGraphHasher.Compute(validated);
            var drifted = validated with
            {
                Schedule = new WorkflowSchedule
                {
                    CronExpression = "15 1 * * *",
                    TimeZone = "UTC",
                    Enabled = false
                }
            };
            await writer.SavePackageAsync(package with { Graph = drifted });

            var version = await writer.CreateVersionAsync(
                package.PackageId,
                hash,
                validated,
                WorkflowPackageValidationResult.Success(hash),
                "author");

            version.Graph.Schedule.Should().BeNull();
            version.Graph.Nodes.Should().ContainSingle(node => node.Parameters["wkb"] == "0101");
            (await writer.GetPackageAsync(package.PackageId))!.Graph.Schedule!.CronExpression.Should().Be("15 1 * * *");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var sql = await File.ReadAllTextAsync(FindMigration());
        sql = sql.Replace("$HonuaSchema$", "\"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"", StringComparison.Ordinal);
        await using var connection = await fixture.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string FindMigration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new DirectoryNotFoundException("Repository root could not be located.");
        }

        return Path.Join(directory.FullName, "src", "Honua.Server", "Migrations", "122_CreateWorkflowPackages.sql");
    }

    private static WorkflowPackage SamplePackage(string packageId)
        => new()
        {
            PackageId = packageId,
            Name = packageId,
            Graph = new WorkflowGraph
            {
                Nodes =
                [
                    new WorkflowNode
                    {
                        NodeId = "area",
                        NodeTypeId = "process:geometry.area",
                        Parameters = new Dictionary<string, string> { ["wkb"] = "0101" }
                    }
                ]
            },
            CreatedAt = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero),
            CreatedBy = "author",
            Metadata = new Dictionary<string, string> { ["source"] = "test" }
        };

    private static WorkflowPublication SamplePublication(string packageId, int version, string publicationId)
        => new()
        {
            PublicationId = publicationId,
            PackageId = packageId,
            PackageVersion = version,
            PackageHash = "hash-1",
            Target = WorkflowPublicationTarget.Schedule,
            Status = WorkflowPublicationStatus.Active,
            Schedule = new WorkflowSchedule { CronExpression = "0 0 * * *", TimeZone = "UTC", Enabled = true },
            WorkflowDefinitionId = $"workflow-package:{packageId}:v{version}",
            EndpointPath = $"/api/v1/console/workflow-publications/{publicationId}/runs",
            Eligibility = WorkflowPackageValidationResult.Success("hash-1"),
            CreatedAt = new DateTimeOffset(2026, 9, 23, 0, 1, 0, TimeSpan.Zero),
            CreatedBy = "author",
            Provenance = new Dictionary<string, string> { ["workflow.packageId"] = packageId }
        };

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

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
