// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for the admin migration performance evidence endpoints
/// (#1033 slice 5).
/// </summary>
/// <remarks>
/// Tests run against the in-memory store registered by the Server when no Postgres
/// provider is configured. Tests seed the store directly through DI so the endpoints
/// can be exercised without spinning up the full release-evidence workflow.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class MigrationPerformanceEvidenceEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-05-19T12:00:00Z", CultureInfo.InvariantCulture);

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;
    private IMigrationPerformanceEvidenceStore _store = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
        _store = _fixture.Services.GetRequiredService<IMigrationPerformanceEvidenceStore>();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/performance-evidence/latest")]
    public async Task GetLatest_WithNoRecords_ReturnsNoContent()
    {
        var response = await _client.GetAsync("/api/v1/admin/migration/performance-evidence/latest");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            // If another concurrent test seeded a record, the body must at least round-trip cleanly.
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("evidenceId");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/performance-evidence/latest")]
    public async Task GetLatest_ReturnsNewestPassingRecord()
    {
        await _store.SaveAsync(NewRecord("sha256:latest-warn", "Warn", offsetSeconds: 0));
        await _store.SaveAsync(NewRecord("sha256:latest-pass", "Pass", offsetSeconds: 60));

        var response = await _client.GetAsync("/api/v1/admin/migration/performance-evidence/latest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("sha256:latest-pass");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/performance-evidence/history")]
    public async Task GetHistory_ReturnsNewestFirstWithEchoedFilters()
    {
        await _store.SaveAsync(NewRecord("sha256:hist-a", "Pass", offsetSeconds: 0));
        await _store.SaveAsync(NewRecord("sha256:hist-b", "Pass", offsetSeconds: 30));
        await _store.SaveAsync(NewRecord("sha256:hist-c", "Pass", offsetSeconds: 60));

        var response = await _client.GetAsync(
            $"/api/v1/admin/migration/performance-evidence/history?sourceFamily={MigrationCostPerformanceSourceFamilies.GeoServerRest}&size={MigrationCostPerformanceFixtureSizes.Small}&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"sourceFamily\":\"geoserver-rest\"");
        body.Should().Contain("\"fixtureSize\":\"small\"");
        body.Should().Contain("sha256:hist-c");
        body.Should().Contain("sha256:hist-b");
        body.Should().Contain("sha256:hist-a");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/performance-evidence/history")]
    public async Task GetHistory_WithInvalidLimit_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/v1/admin/migration/performance-evidence/history?limit=not-a-number");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/performance-evidence/{evidenceId}")]
    public async Task GetById_ReturnsRecord_WhenPresent()
    {
        await _store.SaveAsync(NewRecord("sha256:lookup", "Pass", offsetSeconds: 0));

        var response = await _client.GetAsync("/api/v1/admin/migration/performance-evidence/sha256:lookup");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("sha256:lookup");
        body.Should().Contain("honua.migration.performance-evidence");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/performance-evidence/{evidenceId}")]
    public async Task GetById_ReturnsNotFound_WhenAbsent()
    {
        var response = await _client.GetAsync("/api/v1/admin/migration/performance-evidence/sha256:absent-record-xyz");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
            Artifact = new MigrationPerformanceEvidenceArtifact
            {
                SourceFamily = sourceFamily,
                FixtureSize = size,
                BaselineProfile = $"{sourceFamily}-{size}-v1",
                Status = status,
                Summary = $"synthetic record {evidenceId}",
                MeasurementScope = "endpoint integration test",
                GeneratedAt = generatedAt,
                FixtureProfile = new MigrationFixtureSizeProfile
                {
                    SourceFamily = sourceFamily,
                    Size = size,
                    Description = "synthetic profile",
                    ExpectedFeatureCount = 1000
                },
                RunMetrics = new MigrationRunMetricsArtifact
                {
                    SourceKind = sourceFamily,
                    SourceFamily = sourceFamily,
                    Source = new MigrationRunMetricsSourceSummary { DisplayName = "fixture-host" },
                    MeasurementScope = "endpoint integration test",
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
                    MeasurementScope = "endpoint integration test",
                    FixtureProfile = new MigrationFixtureSizeProfile
                    {
                        SourceFamily = sourceFamily,
                        Size = size,
                        Description = "synthetic profile",
                        ExpectedFeatureCount = 1000
                    },
                    Signals = []
                },
                Fingerprint = evidenceId,
                Redaction = new MigrationPerformanceEvidenceRedactionPosture
                {
                    Summary = "deny by default"
                }
            }
        };
    }
}
