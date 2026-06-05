// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Domain;
using Honua.Postgres.Features.Migration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Issue #1253: <see cref="PostgresMigrationBatchRunCatalog"/> persists batch
/// composition, ordered child rows, per-child status with sticky-terminal
/// semantics, and rolled-up batch counts. Exercises the real SQL (migration 045
/// schema) against the shared Testcontainers Postgres fixture.
/// </summary>
[Collection("Database")]
public sealed class PostgresMigrationBatchRunCatalogTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task CreateAsync_PersistsBatchAndChildren_AndIsIdempotent()
    {
        await EnsureBatchTablesAsync();
        var catalog = CreateCatalog();
        var batchId = $"batch-{Guid.NewGuid():N}"[..16];

        var record = NewBatch(batchId, 2);
        var children = NewChildren(batchId);

        var created = await catalog.CreateAsync(record, manifestBody: "{\"artifactKind\":\"honua.migration.manifest\"}", children);
        created.BatchId.Should().Be(batchId);
        created.TotalChildren.Should().Be(2);

        // Idempotent: re-create with different children is a no-op.
        await catalog.CreateAsync(record with { SourceDisplayName = "ignored" }, manifestBody: null, []);

        var persistedChildren = await catalog.GetChildrenAsync(batchId);
        persistedChildren.Should().HaveCount(2);
        persistedChildren[0].SourceResourceId.Should().Be("resource:x:layer:0");
        persistedChildren[1].DependsOn.Should().ContainSingle().Which.Should().Be("resource:x:layer:0");

        var manifest = await catalog.GetManifestBodyAsync(batchId);
        manifest.Should().Contain("honua.migration.manifest");
    }

    [IntegrationTest]
    public async Task UpdateChildAsync_AdvancesStatus_AndIsStickyTerminal()
    {
        await EnsureBatchTablesAsync();
        var catalog = CreateCatalog();
        var batchId = $"batch-{Guid.NewGuid():N}"[..16];
        await catalog.CreateAsync(NewBatch(batchId, 2), null, NewChildren(batchId));

        var running = await catalog.UpdateChildAsync(
            batchId, 0, MigrationBatchChildStatus.Running, "job-1", null, null, DateTimeOffset.UtcNow);
        running!.Status.Should().Be(MigrationBatchChildStatus.Running);
        running.JobId.Should().Be("job-1");

        var succeeded = await catalog.UpdateChildAsync(
            batchId, 0, MigrationBatchChildStatus.Succeeded, "job-1", 99, null, DateTimeOffset.UtcNow);
        succeeded!.Status.Should().Be(MigrationBatchChildStatus.Succeeded);
        succeeded.PublishedLayerId.Should().Be(99);

        // Terminal state is sticky: a late update is refused.
        var refused = await catalog.UpdateChildAsync(
            batchId, 0, MigrationBatchChildStatus.Failed, "job-1", null, "late", DateTimeOffset.UtcNow);
        refused!.Status.Should().Be(MigrationBatchChildStatus.Succeeded);
    }

    [IntegrationTest]
    public async Task UpdateBatchAsync_RollsUpCounts_AndIsStickyTerminal()
    {
        await EnsureBatchTablesAsync();
        var catalog = CreateCatalog();
        var batchId = $"batch-{Guid.NewGuid():N}"[..16];
        await catalog.CreateAsync(NewBatch(batchId, 2), null, NewChildren(batchId));

        (await catalog.GetActiveBatchIdsAsync()).Should().Contain(batchId);

        var succeeded = await catalog.UpdateBatchAsync(
            batchId, MigrationBatchRunStatus.Succeeded, 2, 0, 0, DateTimeOffset.UtcNow, true, "done");
        succeeded!.Status.Should().Be(MigrationBatchRunStatus.Succeeded);
        succeeded.SucceededChildren.Should().Be(2);
        succeeded.RelationshipsApplied.Should().BeTrue();

        // Terminal batch state is sticky.
        var refused = await catalog.UpdateBatchAsync(
            batchId, MigrationBatchRunStatus.Failed, 0, 2, 0, DateTimeOffset.UtcNow, null, null);
        refused!.Status.Should().Be(MigrationBatchRunStatus.Succeeded);

        (await catalog.GetActiveBatchIdsAsync()).Should().NotContain(batchId);
    }

    private PostgresMigrationBatchRunCatalog CreateCatalog()
        => new(fixture.ConnectionString, NullLogger<PostgresMigrationBatchRunCatalog>.Instance);

    private static MigrationBatchRunRecord NewBatch(string batchId, int total) => new()
    {
        BatchId = batchId,
        SourceKind = "arcgis-geoservices-rest",
        SourceUrl = "https://example.com/FeatureServer",
        Status = MigrationBatchRunStatus.Running,
        StartedAt = DateTimeOffset.UtcNow,
        TotalChildren = total,
        ApplyRelationships = true
    };

    private static MigrationBatchChildRecord[] NewChildren(string batchId) =>
    [
        new MigrationBatchChildRecord
        {
            BatchId = batchId,
            Ordinal = 0,
            SourceResourceId = "resource:x:layer:0",
            ServiceUrl = "https://example.com/FeatureServer",
            SourceLayerId = 0,
            TableName = "origin",
            Status = MigrationBatchChildStatus.Pending,
            UpdatedAt = DateTimeOffset.UtcNow
        },
        new MigrationBatchChildRecord
        {
            BatchId = batchId,
            Ordinal = 1,
            SourceResourceId = "resource:x:layer:1",
            ServiceUrl = "https://example.com/FeatureServer",
            SourceLayerId = 1,
            TableName = "related",
            DependsOn = ["resource:x:layer:0"],
            Status = MigrationBatchChildStatus.Pending,
            UpdatedAt = DateTimeOffset.UtcNow
        }
    ];

    /// <summary>
    /// Migration 045 creates the batch tables; tests apply the same schema
    /// directly so they do not depend on the migration runner.
    /// </summary>
    private async Task EnsureBatchTablesAsync()
    {
        await using var conn = await fixture.GetConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE SCHEMA IF NOT EXISTS honua;
            CREATE TABLE IF NOT EXISTS honua.migration_batch_runs (
                batch_id                VARCHAR(64)  PRIMARY KEY,
                source_kind             VARCHAR(64)  NOT NULL,
                source_url              TEXT         NOT NULL DEFAULT '',
                source_display_name     TEXT,
                status                  VARCHAR(32)  NOT NULL DEFAULT 'running',
                started_at              TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
                completed_at            TIMESTAMPTZ,
                total_children          INTEGER      NOT NULL DEFAULT 0,
                succeeded_children      INTEGER      NOT NULL DEFAULT 0,
                failed_children         INTEGER      NOT NULL DEFAULT 0,
                cancelled_children      INTEGER      NOT NULL DEFAULT 0,
                apply_relationships     BOOLEAN      NOT NULL DEFAULT FALSE,
                relationships_applied   BOOLEAN      NOT NULL DEFAULT FALSE,
                manifest_body           JSONB,
                status_note             TEXT,
                CONSTRAINT chk_migration_batch_runs_status
                    CHECK (status IN ('running','succeeded','failed','cancelled','needs-review'))
            );
            CREATE TABLE IF NOT EXISTS honua.migration_batch_children (
                batch_id            VARCHAR(64)  NOT NULL
                    REFERENCES honua.migration_batch_runs (batch_id) ON DELETE CASCADE,
                ordinal             INTEGER      NOT NULL,
                source_resource_id  TEXT         NOT NULL,
                service_url         TEXT         NOT NULL,
                source_layer_id     INTEGER      NOT NULL,
                table_name          TEXT         NOT NULL,
                target_schema       TEXT,
                service_name        TEXT,
                depends_on          JSONB        NOT NULL DEFAULT '[]'::jsonb,
                status              VARCHAR(32)  NOT NULL DEFAULT 'pending',
                job_id              VARCHAR(64),
                published_layer_id  INTEGER,
                status_note         TEXT,
                updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
                PRIMARY KEY (batch_id, ordinal),
                CONSTRAINT chk_migration_batch_children_status
                    CHECK (status IN ('pending','running','succeeded','failed','needs-review','cancelled'))
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}
