// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Xunit;

namespace Honua.Core.Tests.Features.Migration;

/// <summary>
/// Unit tests for <see cref="InMemoryMigrationRunCatalog"/> (#1598): idempotent start,
/// sticky terminal state, evidence-pack and scorecard round-trips, and list
/// ordering/filtering/paging semantics.
/// </summary>
public sealed class InMemoryMigrationRunCatalogTests
{
    private readonly InMemoryMigrationRunCatalog _catalog = new();

    [Fact]
    public async Task RecordStartedAsync_NewRun_PersistsRunningRecord()
    {
        var record = NewRunningRecord("run-1");

        var persisted = await _catalog.RecordStartedAsync(record);

        persisted.Should().Be(record);
        (await _catalog.GetAsync("run-1")).Should().Be(record);
    }

    [Fact]
    public async Task RecordStartedAsync_DuplicateRunId_ReturnsExistingRowUntouched()
    {
        var original = NewRunningRecord("run-1") with { SourceDisplayName = "Original" };
        await _catalog.RecordStartedAsync(original);

        var replay = await _catalog.RecordStartedAsync(original with { SourceDisplayName = "Replay" });

        replay.SourceDisplayName.Should().Be("Original");
        (await _catalog.GetAsync("run-1"))!.SourceDisplayName.Should().Be("Original");
    }

    [Fact]
    public async Task RecordCompletedAsync_RunningRun_UpdatesStatusAndEvidencePack()
    {
        await _catalog.RecordStartedAsync(NewRunningRecord("run-1"));
        var completedAt = DateTimeOffset.UtcNow;

        var completed = await _catalog.RecordCompletedAsync(
            "run-1",
            MigrationRunStatus.Succeeded,
            completedAt,
            evidencePackRef: "ref-1",
            evidencePackFingerprint: "sha256:abc",
            evidencePackBody: """{"kind":"evidence-pack"}""",
            statusNote: "2/2 layer(s) succeeded.");

        completed.Should().NotBeNull();
        completed!.Status.Should().Be(MigrationRunStatus.Succeeded);
        completed.CompletedAt.Should().Be(completedAt);
        completed.EvidencePackRef.Should().Be("ref-1");
        completed.EvidencePackFingerprint.Should().Be("sha256:abc");
        completed.StatusNote.Should().Be("2/2 layer(s) succeeded.");
        (await _catalog.GetEvidencePackAsync("run-1")).Should().Be("""{"kind":"evidence-pack"}""");
    }

    [Fact]
    public async Task RecordCompletedAsync_AlreadyTerminal_DoesNotOverwrite()
    {
        await _catalog.RecordStartedAsync(NewRunningRecord("run-1"));
        await _catalog.CancelAsync("run-1", DateTimeOffset.UtcNow, "operator cancelled");

        var lateSuccess = await _catalog.RecordCompletedAsync(
            "run-1",
            MigrationRunStatus.Succeeded,
            DateTimeOffset.UtcNow,
            evidencePackRef: null,
            evidencePackFingerprint: null,
            evidencePackBody: null,
            statusNote: null);

        lateSuccess!.Status.Should().Be(MigrationRunStatus.Cancelled);
        lateSuccess.StatusNote.Should().Be("operator cancelled");
    }

    [Fact]
    public async Task RecordCompletedAsync_UnknownRun_ReturnsNull()
    {
        var result = await _catalog.RecordCompletedAsync(
            "missing",
            MigrationRunStatus.Succeeded,
            DateTimeOffset.UtcNow,
            evidencePackRef: null,
            evidencePackFingerprint: null,
            evidencePackBody: null,
            statusNote: null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CancelAsync_RunningRun_MarksCancelled()
    {
        await _catalog.RecordStartedAsync(NewRunningRecord("run-1"));
        var cancelledAt = DateTimeOffset.UtcNow;

        var cancelled = await _catalog.CancelAsync("run-1", cancelledAt, "stop");

        cancelled!.Status.Should().Be(MigrationRunStatus.Cancelled);
        cancelled.CompletedAt.Should().Be(cancelledAt);
        cancelled.StatusNote.Should().Be("stop");
    }

    [Fact]
    public async Task ListAsync_OrdersMostRecentFirst_AndPages()
    {
        var now = DateTimeOffset.UtcNow;
        await _catalog.RecordStartedAsync(NewRunningRecord("run-old") with { StartedAt = now.AddMinutes(-10) });
        await _catalog.RecordStartedAsync(NewRunningRecord("run-new") with { StartedAt = now });
        await _catalog.RecordStartedAsync(NewRunningRecord("run-mid") with { StartedAt = now.AddMinutes(-5) });

        var page = await _catalog.ListAsync(new MigrationRunListQuery { Limit = 2, Offset = 1 });

        page.TotalCount.Should().Be(3);
        page.Limit.Should().Be(2);
        page.Offset.Should().Be(1);
        page.Items.Select(static r => r.RunId).Should().Equal("run-mid", "run-old");
    }

    [Fact]
    public async Task ListAsync_FiltersBySourceKindAndStatus()
    {
        await _catalog.RecordStartedAsync(NewRunningRecord("run-1") with { SourceKind = "geoserver-rest" });
        await _catalog.RecordStartedAsync(NewRunningRecord("run-2"));
        await _catalog.RecordCompletedAsync(
            "run-2",
            MigrationRunStatus.Failed,
            DateTimeOffset.UtcNow,
            evidencePackRef: null,
            evidencePackFingerprint: null,
            evidencePackBody: null,
            statusNote: null);

        var byKind = await _catalog.ListAsync(new MigrationRunListQuery { SourceKind = "GEOSERVER-REST" });
        byKind.Items.Should().ContainSingle(static r => r.RunId == "run-1");

        var byStatus = await _catalog.ListAsync(new MigrationRunListQuery { Status = MigrationRunStatus.Failed });
        byStatus.Items.Should().ContainSingle(static r => r.RunId == "run-2");
    }

    [Fact]
    public async Task ListAsync_ClampsLimitToMaximum()
    {
        await _catalog.RecordStartedAsync(NewRunningRecord("run-1"));

        var page = await _catalog.ListAsync(new MigrationRunListQuery { Limit = 5000, Offset = -3 });

        page.Limit.Should().Be(100);
        page.Offset.Should().Be(0);
    }

    [Fact]
    public async Task RecordScorecardAsync_KnownRun_PersistsFingerprintAndBody()
    {
        await _catalog.RecordStartedAsync(NewRunningRecord("run-1"));

        var updated = await _catalog.RecordScorecardAsync("run-1", "sha256:def", """{"kind":"scorecard"}""");

        updated!.ReconciliationScorecardFingerprint.Should().Be("sha256:def");
        (await _catalog.GetScorecardAsync("run-1")).Should().Be("""{"kind":"scorecard"}""");
    }

    [Fact]
    public async Task RecordScorecardAsync_UnknownRun_ReturnsNull()
    {
        var updated = await _catalog.RecordScorecardAsync("missing", "sha256:def", "{}");

        updated.Should().BeNull();
        (await _catalog.GetScorecardAsync("missing")).Should().BeNull();
    }

    private static MigrationRunRecord NewRunningRecord(string runId) => new()
    {
        RunId = runId,
        SourceKind = "arcgis-geoservices-rest",
        SourceUrl = "https://example.com/arcgis/rest/services",
        Status = MigrationRunStatus.Running,
        StartedAt = DateTimeOffset.UtcNow
    };
}
