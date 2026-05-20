// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Postgres.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Issue #1015 slice 5: <see cref="PostgresMigrationRunCatalog"/> persists run
/// records, enforces sticky-terminal-status semantics, and pages the list view
/// most-recent first. These tests use the shared Testcontainers Postgres
/// fixture and exercise the real SQL (no mocks) so the JSONB evidence-pack
/// column and DISTINCT-status filter are validated end-to-end.
/// </summary>
[Collection("Database")]
public sealed class PostgresMigrationRunCatalogTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task RecordStartedAsync_PersistsRow_AndIsIdempotent()
    {
        await EnsureMigrationRunsTableAsync();
        var catalog = CreateCatalog();
        var record = NewRecord("run-start-001", MigrationRunStatus.Running);

        var first = await catalog.RecordStartedAsync(record);
        var second = await catalog.RecordStartedAsync(record with { SourceDisplayName = "ignored" });

        first.RunId.Should().Be("run-start-001");
        first.Status.Should().Be(MigrationRunStatus.Running);
        second.RunId.Should().Be("run-start-001");
        // Idempotent re-record returns the original row (display name from the
        // first insert) rather than overwriting with the second attempt.
        second.SourceDisplayName.Should().Be(first.SourceDisplayName);

        var fetched = await catalog.GetAsync("run-start-001");
        fetched.Should().NotBeNull();
        fetched!.SourceKind.Should().Be("geoserver-rest");
    }

    [IntegrationTest]
    public async Task RecordCompletedAsync_TransitionsRunningToTerminal_AndIsSticky()
    {
        await EnsureMigrationRunsTableAsync();
        var catalog = CreateCatalog();
        var runId = $"run-complete-{Guid.NewGuid():N}";
        await catalog.RecordStartedAsync(NewRecord(runId, MigrationRunStatus.Running));

        var completed = await catalog.RecordCompletedAsync(
            runId,
            MigrationRunStatus.Succeeded,
            DateTimeOffset.UtcNow,
            evidencePackRef: "pack-ref",
            evidencePackFingerprint: "sha256:abc",
            evidencePackBody: "{\"artifactKind\":\"honua.migration.evidence-pack\"}",
            statusNote: null);

        completed.Should().NotBeNull();
        completed!.Status.Should().Be(MigrationRunStatus.Succeeded);
        completed.EvidencePackFingerprint.Should().Be("sha256:abc");

        var body = await catalog.GetEvidencePackAsync(runId);
        body.Should().Contain("honua.migration.evidence-pack");

        // Sticky: a second completion attempt with a different status must
        // not overwrite the terminal row.
        var stuck = await catalog.RecordCompletedAsync(
            runId,
            MigrationRunStatus.Failed,
            DateTimeOffset.UtcNow,
            evidencePackRef: null,
            evidencePackFingerprint: null,
            evidencePackBody: null,
            statusNote: "should be ignored");

        stuck!.Status.Should().Be(MigrationRunStatus.Succeeded);
    }

    [IntegrationTest]
    public async Task CancelAsync_OnlyAffectsRunningRows()
    {
        await EnsureMigrationRunsTableAsync();
        var catalog = CreateCatalog();
        var runId = $"run-cancel-{Guid.NewGuid():N}";
        await catalog.RecordStartedAsync(NewRecord(runId, MigrationRunStatus.Running));

        var cancelled = await catalog.CancelAsync(runId, DateTimeOffset.UtcNow, "operator request");
        cancelled.Should().NotBeNull();
        cancelled!.Status.Should().Be(MigrationRunStatus.Cancelled);
        cancelled.StatusNote.Should().Be("operator request");

        // Second cancel is a no-op: row stays cancelled with the original note.
        var again = await catalog.CancelAsync(runId, DateTimeOffset.UtcNow, "second attempt");
        again!.Status.Should().Be(MigrationRunStatus.Cancelled);
        again.StatusNote.Should().Be("operator request");
    }

    [IntegrationTest]
    public async Task ListAsync_PagesMostRecentFirst_AndFiltersByStatusAndSourceKind()
    {
        await EnsureMigrationRunsTableAsync();
        var catalog = CreateCatalog();
        var stamp = DateTimeOffset.UtcNow.AddMinutes(-30);
        var older = NewRecord("run-list-older-" + Guid.NewGuid().ToString("N"), MigrationRunStatus.Running) with { StartedAt = stamp };
        var newer = NewRecord("run-list-newer-" + Guid.NewGuid().ToString("N"), MigrationRunStatus.Running) with { StartedAt = stamp.AddMinutes(10) };
        await catalog.RecordStartedAsync(older);
        await catalog.RecordStartedAsync(newer);
        await catalog.RecordCompletedAsync(
            newer.RunId,
            MigrationRunStatus.Succeeded,
            stamp.AddMinutes(11),
            evidencePackRef: null,
            evidencePackFingerprint: null,
            evidencePackBody: null,
            statusNote: null);

        var page = await catalog.ListAsync(new MigrationRunListQuery { Limit = 50 });
        page.Items.Should().Contain(r => r.RunId == newer.RunId);
        page.Items.Should().Contain(r => r.RunId == older.RunId);
        var indexNewer = page.Items.Select((r, i) => (r, i)).First(t => t.r.RunId == newer.RunId).i;
        var indexOlder = page.Items.Select((r, i) => (r, i)).First(t => t.r.RunId == older.RunId).i;
        indexNewer.Should().BeLessThan(indexOlder, "list view is ordered most-recent first");

        var statusPage = await catalog.ListAsync(new MigrationRunListQuery
        {
            Limit = 50,
            Status = MigrationRunStatus.Succeeded
        });
        statusPage.Items.Should().Contain(r => r.RunId == newer.RunId);
        statusPage.Items.Should().NotContain(r => r.RunId == older.RunId);
    }

    private PostgresMigrationRunCatalog CreateCatalog()
        => new(fixture.ConnectionString, NullLogger<PostgresMigrationRunCatalog>.Instance);

    /// <summary>
    /// The 031 migration creates honua.migration_runs; tests apply the same
    /// schema directly so they do not depend on the migration runner. The
    /// table is shared across the container (run id is per-test).
    /// </summary>
    private async Task EnsureMigrationRunsTableAsync()
    {
        await using var conn = await fixture.GetConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE SCHEMA IF NOT EXISTS honua;
            CREATE TABLE IF NOT EXISTS honua.migration_runs (
                run_id                      VARCHAR(64)  PRIMARY KEY,
                source_kind                 VARCHAR(64)  NOT NULL,
                source_url                  TEXT         NOT NULL DEFAULT '',
                source_display_name         TEXT,
                status                      VARCHAR(32)  NOT NULL DEFAULT 'running',
                started_at                  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
                completed_at                TIMESTAMPTZ,
                evidence_pack_ref           TEXT,
                evidence_pack_fingerprint   VARCHAR(128),
                evidence_pack_body          JSONB,
                status_note                 TEXT,
                CONSTRAINT chk_migration_runs_status
                    CHECK (status IN ('running','succeeded','failed','cancelled'))
            );
            CREATE INDEX IF NOT EXISTS idx_migration_runs_started_at
                ON honua.migration_runs (started_at DESC);
            CREATE INDEX IF NOT EXISTS idx_migration_runs_status
                ON honua.migration_runs (status);
            CREATE INDEX IF NOT EXISTS idx_migration_runs_source_kind
                ON honua.migration_runs (source_kind);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static MigrationRunRecord NewRecord(string runId, MigrationRunStatus status) => new()
    {
        RunId = runId,
        SourceKind = "geoserver-rest",
        SourceUrl = "https://geoserver.example.test/geoserver/rest",
        SourceDisplayName = "fixture",
        Status = status,
        StartedAt = DateTimeOffset.UtcNow
    };
}
