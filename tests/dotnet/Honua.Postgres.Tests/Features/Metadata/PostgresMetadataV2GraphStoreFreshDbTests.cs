// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Metadata;

/// <summary>
/// Regression tests for honua-server#1341 — on a fresh-DB container where migration
/// 031 has not created the Metadata v2 tables, the admin layer-publish path 500'd
/// with relation "honua.metadata_v2_current" does not exist (42P01). The store now
/// tolerates the missing relation on read and self-heals the schema on write, so the
/// publish path bootstraps an empty graph and succeeds instead of failing.
/// </summary>
[Collection("Database")]
public sealed class PostgresMetadataV2GraphStoreFreshDbTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task GetCurrentAsync_WhenMetadataV2TablesMissing_ThrowsInvalidOperationInsteadOfRawPostgresError()
    {
        // Isolated schema that deliberately does NOT create the metadata_v2 tables —
        // exactly the fresh-DB shape that surfaced the 500.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreFreshDbTests));
        try
        {
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(provider, environment: "Test", schemaName: schema);

            var act = () => store.GetCurrentAsync().AsTask();

            // The publish path catches InvalidOperationException to bootstrap an empty
            // graph. A raw PostgresException (42P01) would bubble out as a 500.
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SaveAsync_OnFreshDbWithoutMetadataV2Tables_SelfHealsSchemaAndActivatesSnapshot()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreFreshDbTests));
        try
        {
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(provider, environment: "Test", schemaName: schema);

            // Mirrors the publish path: no current snapshot exists, so start from an
            // empty graph and force the first write (null expectedEtag).
            var graph = new MetadataV2Graph
            {
                Environment = "Test",
                Revision = 1,
                GeneratedAt = DateTimeOffset.UtcNow,
                Resources =
                [
                    new MetadataV2Resource
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = "res-layer-9000", Name = "fresh-db-layer" },
                        Type = MetadataV2ResourceType.FeatureDataset,
                    },
                ],
            };

            var saved = await store.SaveAsync(graph, expectedEtag: null);
            saved.Should().NotBeNull();

            // The current pointer + snapshot now resolve — the publish round-trip works.
            var current = await store.GetCurrentAsync();
            current.Graph.Revision.Should().Be(1);
            current.Graph.Resources.Should().ContainSingle(resource => resource.Metadata.Id == "res-layer-9000");

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT COUNT(*)::int
                FROM "{schema}".metadata_v2_current c
                JOIN "{schema}".metadata_v2_snapshots s
                  ON s.environment = c.environment AND s.revision = c.revision
                WHERE c.environment = 'Test';
                """;
            var currentCount = (int)(await command.ExecuteScalarAsync())!;
            currentCount.Should().Be(1, "SaveAsync must create and activate the snapshot on a fresh DB");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

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
