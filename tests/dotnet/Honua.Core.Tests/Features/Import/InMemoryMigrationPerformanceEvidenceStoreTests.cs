// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Behaviour tests for <see cref="InMemoryMigrationPerformanceEvidenceStore"/> (issue #1033 slice 5).
/// </summary>
public sealed class InMemoryMigrationPerformanceEvidenceStoreTests
{
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-05-19T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task SaveAndGetById_RoundTripsRecord()
    {
        var store = new InMemoryMigrationPerformanceEvidenceStore();
        var record = NewRecord("sha256:aaa", "Pass", offsetSeconds: 0);

        await store.SaveAsync(record);
        var loaded = await store.GetByIdAsync("sha256:aaa");

        loaded.Should().NotBeNull();
        loaded!.EvidenceId.Should().Be("sha256:aaa");
        loaded.Status.Should().Be("Pass");
    }

    [Fact]
    public async Task SaveAsync_IsIdempotent_OnSameId()
    {
        var store = new InMemoryMigrationPerformanceEvidenceStore();
        await store.SaveAsync(NewRecord("sha256:dup", "Pass", offsetSeconds: 0));
        await store.SaveAsync(NewRecord("sha256:dup", "Warn", offsetSeconds: 60));

        var history = await store.GetHistoryAsync(sourceFamily: null, fixtureSize: null, limit: 10);
        history.Should().HaveCount(1);
        history[0].Status.Should().Be("Warn");
    }

    [Fact]
    public async Task GetLatestPassing_ReturnsNewestPassRecord()
    {
        var store = new InMemoryMigrationPerformanceEvidenceStore();
        await store.SaveAsync(NewRecord("sha256:a", "Pass", offsetSeconds: 0));
        await store.SaveAsync(NewRecord("sha256:b", "Fail", offsetSeconds: 30));
        await store.SaveAsync(NewRecord("sha256:c", "Pass", offsetSeconds: 60));
        await store.SaveAsync(NewRecord("sha256:d", "Warn", offsetSeconds: 90));

        var latest = await store.GetLatestPassingAsync();
        latest.Should().NotBeNull();
        latest!.EvidenceId.Should().Be("sha256:c");
    }

    [Fact]
    public async Task GetLatestPassing_ReturnsNull_WhenNoPassRecords()
    {
        var store = new InMemoryMigrationPerformanceEvidenceStore();
        await store.SaveAsync(NewRecord("sha256:a", "Warn", offsetSeconds: 0));

        (await store.GetLatestPassingAsync()).Should().BeNull();
    }

    [Fact]
    public async Task GetHistory_ReturnsNewestFirst_AndAppliesLimit()
    {
        var store = new InMemoryMigrationPerformanceEvidenceStore();
        for (var i = 0; i < 5; i++)
        {
            await store.SaveAsync(NewRecord($"sha256:r{i}", "Pass", offsetSeconds: i * 60));
        }

        var history = await store.GetHistoryAsync(sourceFamily: null, fixtureSize: null, limit: 3);

        history.Should().HaveCount(3);
        history[0].EvidenceId.Should().Be("sha256:r4");
        history[1].EvidenceId.Should().Be("sha256:r3");
        history[2].EvidenceId.Should().Be("sha256:r2");
    }

    [Fact]
    public async Task GetHistory_FiltersBySourceFamilyAndSize()
    {
        var store = new InMemoryMigrationPerformanceEvidenceStore();
        await store.SaveAsync(NewRecord("sha256:gs-s", "Pass", offsetSeconds: 0,
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            size: MigrationCostPerformanceFixtureSizes.Small));
        await store.SaveAsync(NewRecord("sha256:gs-m", "Pass", offsetSeconds: 30,
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            size: MigrationCostPerformanceFixtureSizes.Medium));
        await store.SaveAsync(NewRecord("sha256:agis-s", "Pass", offsetSeconds: 60,
            sourceFamily: MigrationCostPerformanceSourceFamilies.ArcGisGeoServicesRest,
            size: MigrationCostPerformanceFixtureSizes.Small));

        var geoServerOnly = await store.GetHistoryAsync(
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            fixtureSize: null,
            limit: 10);
        geoServerOnly.Should().HaveCount(2);

        var smallOnly = await store.GetHistoryAsync(
            sourceFamily: null,
            fixtureSize: MigrationCostPerformanceFixtureSizes.Small,
            limit: 10);
        smallOnly.Should().HaveCount(2);

        var geoServerSmall = await store.GetHistoryAsync(
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            fixtureSize: MigrationCostPerformanceFixtureSizes.Small,
            limit: 10);
        geoServerSmall.Should().HaveCount(1);
        geoServerSmall[0].EvidenceId.Should().Be("sha256:gs-s");
    }

    [Fact]
    public async Task GetHistory_ClampsExcessiveLimits()
    {
        var store = new InMemoryMigrationPerformanceEvidenceStore();
        for (var i = 0; i < 250; i++)
        {
            await store.SaveAsync(NewRecord($"sha256:r{i:000}", "Pass", offsetSeconds: i));
        }

        var history = await store.GetHistoryAsync(null, null, limit: 1_000);
        history.Count.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public async Task GetById_ReturnsNull_ForUnknownId()
    {
        var store = new InMemoryMigrationPerformanceEvidenceStore();
        (await store.GetByIdAsync("sha256:missing")).Should().BeNull();
    }

    private static MigrationPerformanceEvidenceRecord NewRecord(
        string evidenceId,
        string status,
        int offsetSeconds,
        string sourceFamily = MigrationCostPerformanceSourceFamilies.GeoServerRest,
        string size = MigrationCostPerformanceFixtureSizes.Small)
    {
        var generatedAt = BaseTime.AddSeconds(offsetSeconds);
        return new MigrationPerformanceEvidenceRecord
        {
            EvidenceId = evidenceId,
            SourceFamily = sourceFamily,
            FixtureSize = size,
            Status = status,
            GeneratedAt = generatedAt,
            Fingerprint = evidenceId,
            Artifact = BuildArtifact(evidenceId, status, generatedAt, sourceFamily, size)
        };
    }

    private static MigrationPerformanceEvidenceArtifact BuildArtifact(
        string evidenceId,
        string status,
        DateTimeOffset generatedAt,
        string sourceFamily,
        string size)
        => new()
        {
            SourceFamily = sourceFamily,
            FixtureSize = size,
            BaselineProfile = $"{sourceFamily}-{size}-v1",
            Status = status,
            Summary = $"synthetic record {evidenceId}",
            MeasurementScope = "unit test",
            GeneratedAt = generatedAt,
            FixtureProfile = new MigrationFixtureSizeProfile
            {
                SourceFamily = sourceFamily,
                Size = size,
                Description = $"{sourceFamily}-{size} synthetic profile",
                ExpectedFeatureCount = 1000,
                ExpectedResourceCount = 10
            },
            RunMetrics = new MigrationRunMetricsArtifact
            {
                SourceKind = sourceFamily,
                SourceFamily = sourceFamily,
                Source = new MigrationRunMetricsSourceSummary { DisplayName = "fixture-host" },
                MeasurementScope = "unit test",
                StartedAt = generatedAt.AddMinutes(-1),
                CompletedAt = generatedAt,
                Totals = new MigrationRunMetricsValues { DurationMilliseconds = 60_000 },
                Privacy = new MigrationRunMetricsPrivacySummary()
            },
            BaselineEvaluation = new MigrationRunMetricsBaselineArtifact
            {
                SourceFamily = sourceFamily,
                Size = size,
                BaselineProfile = $"{sourceFamily}-{size}-v1",
                Status = status,
                Summary = $"synthetic baseline {status}",
                MeasurementScope = "unit test",
                FixtureProfile = new MigrationFixtureSizeProfile
                {
                    SourceFamily = sourceFamily,
                    Size = size,
                    Description = $"{sourceFamily}-{size} synthetic profile",
                    ExpectedFeatureCount = 1000,
                    ExpectedResourceCount = 10
                },
                Signals = []
            },
            Fingerprint = evidenceId,
            Redaction = new MigrationPerformanceEvidenceRedactionPosture
            {
                Summary = "deny by default"
            }
        };
}
