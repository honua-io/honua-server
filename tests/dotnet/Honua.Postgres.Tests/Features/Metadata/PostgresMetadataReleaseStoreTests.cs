// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Tests.Features.Metadata;

[Collection("Database")]
public sealed class PostgresMetadataReleaseStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task EnvironmentReader_AndReleasePackageStore_RoundTripMetadataReleasePackage()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataReleaseStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            await InsertSnapshotAsync(schema, BuildGraph("dev", 41), "etag-dev-41");

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var reader = new PostgresMetadataV2EnvironmentSnapshotReader(provider, schema);
            var snapshot = await reader.GetCurrentAsync("dev");

            snapshot.Should().NotBeNull();
            snapshot!.Revision.Should().Be(41);
            snapshot.Etag.Should().Be("etag-dev-41");
            snapshot.Graph.Resources.Should().ContainSingle(resource => resource.Metadata.Id == "res.parcels");

            var store = new PostgresMetadataReleasePackageStore(provider, schema);
            var packageId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var package = new MetadataReleasePackage
            {
                PackageId = packageId,
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = packageId.ToString("D"),
                    Name = "promote-parcels",
                    Namespace = "ops",
                    Title = "Promote parcels",
                },
                SourceEnvironment = "dev",
                SourceRevision = 41,
                SourceEtag = "etag-dev-41",
                TargetEnvironments = ["staging"],
                Entries =
                [
                    new MetadataReleaseEntry
                    {
                        SemanticId = "res.parcels",
                        ArtifactKind = MetadataSemanticArtifactKind.Resource,
                        ResourceType = MetadataV2ResourceType.FeatureDataset,
                        DesiredMetadataRevision = 41,
                        DesiredContentVersionId = "content-v1",
                        TargetStates =
                        [
                            new MetadataReleaseTargetState
                            {
                                Environment = "staging",
                                CurrentMetadataRevision = 7,
                                BindingState = MetadataEnvironmentBindingState.Bound,
                            },
                        ],
                    },
                ],
                CreatedBy = "user-1",
                CreatedAt = now,
                UpdatedAt = now,
            };

            await store.CreateAsync(package);
            var loaded = await store.GetAsync(packageId);

            loaded.Should().NotBeNull();
            loaded!.Metadata.Name.Should().Be("promote-parcels");
            loaded.Metadata.Namespace.Should().Be("ops");
            loaded.SourceEnvironment.Should().Be("dev");
            loaded.SourceRevision.Should().Be(41);
            loaded.SourceEtag.Should().Be("etag-dev-41");
            loaded.TargetEnvironments.Should().Equal("staging");
            loaded.Entries.Should().ContainSingle(entry =>
                entry.SemanticId == "res.parcels" &&
                entry.DesiredContentVersionId == "content-v1" &&
                entry.TargetStates.Single().CurrentMetadataRevision == 7);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task EnsureTablesAsync(string schema)
    {
        await using var connection = await fixtureConnection(schema).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS "{schema}".metadata_v2_snapshots (
                environment       TEXT          NOT NULL,
                revision          BIGINT        NOT NULL,
                schema_version    TEXT          NOT NULL,
                api_version       TEXT          NOT NULL,
                document          JSONB         NOT NULL,
                etag              TEXT          NOT NULL,
                generated_at      TIMESTAMPTZ   NOT NULL,
                created_at        TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
                PRIMARY KEY (environment, revision)
            );

            CREATE TABLE IF NOT EXISTS "{schema}".metadata_v2_current (
                environment       TEXT          NOT NULL PRIMARY KEY,
                revision          BIGINT        NOT NULL,
                etag              TEXT          NOT NULL,
                activated_at      TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
                FOREIGN KEY (environment, revision)
                    REFERENCES "{schema}".metadata_v2_snapshots(environment, revision)
                    ON DELETE RESTRICT
            );

            CREATE TABLE IF NOT EXISTS "{schema}".metadata_v2_release_packages (
                package_id          UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
                package_key         TEXT        NOT NULL,
                package_namespace   TEXT        NULL,
                status              TEXT        NOT NULL DEFAULT 'draft',
                source_environment  TEXT        NOT NULL,
                source_revision     BIGINT      NOT NULL,
                source_etag         TEXT        NOT NULL,
                target_environments JSONB       NOT NULL,
                entries             JSONB       NOT NULL,
                package_metadata    JSONB       NOT NULL,
                created_by          TEXT        NOT NULL,
                created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                CONSTRAINT metadata_v2_release_packages_status
                    CHECK (status IN ('draft','ready','staged','superseded','cancelled'))
            );
            """;
        await command.ExecuteNonQueryAsync();

        async Task<NpgsqlConnection> fixtureConnection(string schemaName)
        {
            var dataSource = fixture.DataSource;
            var conn = await dataSource.OpenConnectionAsync();
            await using var searchPath = conn.CreateCommand();
            searchPath.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await searchPath.ExecuteNonQueryAsync();
            return conn;
        }
    }

    private async Task InsertSnapshotAsync(string schema, MetadataV2Graph graph, string etag)
    {
        var json = JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO "{schema}".metadata_v2_snapshots
                (environment, revision, schema_version, api_version, document, etag, generated_at)
            VALUES
                (@environment, @revision, @schema_version, @api_version, @document, @etag, @generated_at);

            INSERT INTO "{schema}".metadata_v2_current (environment, revision, etag)
            VALUES (@environment, @revision, @etag);
            """;
        command.Parameters.AddWithValue("@environment", graph.Environment);
        command.Parameters.AddWithValue("@revision", graph.Revision);
        command.Parameters.AddWithValue("@schema_version", graph.SchemaVersion);
        command.Parameters.AddWithValue("@api_version", graph.ApiVersion);
        command.Parameters.Add(new NpgsqlParameter("@document", NpgsqlDbType.Jsonb) { Value = json });
        command.Parameters.AddWithValue("@etag", etag);
        command.Parameters.AddWithValue("@generated_at", graph.GeneratedAt);
        await command.ExecuteNonQueryAsync();
    }

    private static MetadataV2Graph BuildGraph(string environment, long revision)
        => new()
        {
            Environment = environment,
            Revision = revision,
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res.parcels", Name = "parcels" },
                    Type = MetadataV2ResourceType.FeatureDataset,
                },
            ],
        };

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return conn;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var conn = await OpenConnectionAsync(cancellationToken);
            try
            {
                var tx = await conn.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (conn, tx);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => operation();
    }
}
