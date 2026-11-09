// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin.Jobs;

public sealed partial class ConsoleJobEndpointsTests
{
    [IntegrationTheory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/artifacts")]
    public async Task GetJobDiagnostics_ResultRetentionBoundary_AgreesWithArtifactListing(long offsetTicks, bool expired)
    {
        var completedAt = _retentionClock.GetUtcNow();
        var retention = _fixture.Services.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>().CurrentValue.ResultRetention;
        _retentionClock.UtcNow = completedAt + retention + TimeSpan.FromTicks(offsetTicks);
        _jobStore.Set(CreateJob("job-result-retention", ExecutionJobStatus.Succeeded, completedAt.AddMinutes(-1), "corr-retention") with
        {
            CompletedAt = completedAt,
            UpdatedAt = _retentionClock.GetUtcNow(),
            ArtifactReferences = ["https://example.test/results/output.json", "data:text/plain,private-result-value"]
        });

        using var response = await _client.GetAsync("/api/v1/admin/jobs/job-result-retention/diagnostics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var diagnostic = await ReadJsonAsync(response);
        var artifacts = diagnostic.RootElement.GetProperty("artifacts");
        artifacts.GetProperty("state").GetString().Should().Be(expired ? "unavailable" : "complete");
        artifacts.GetProperty("expiredCount").GetInt32().Should().Be(expired ? 2 : 0);
        artifacts.GetProperty("availableCount").GetInt32().Should().Be(expired ? 0 : 2);

        using var listingResponse = await _client.GetAsync("/api/v1/admin/jobs/job-result-retention/artifacts");
        listingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listing = await ReadJsonAsync(listingResponse);
        var items = listing.RootElement.GetProperty("items").EnumerateArray().ToArray();
        items.Should().HaveCount(2);
        items[0].GetProperty("availability").GetString().Should().Be(expired ? "Expired" : "Available");
        items[1].GetProperty("availability").GetString().Should().Be(expired ? "Expired" : "Redacted");
        if (expired)
        {
            foreach (var item in items) item.TryGetProperty("providerLink", out _).Should().BeFalse();
        }
        listing.RootElement.GetRawText().Should().NotContain("private-result-value");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_NonGpResults_PreserveOwnArtifactLifetime()
    {
        var completedAt = _retentionClock.GetUtcNow();
        _retentionClock.UtcNow = completedAt.AddDays(365);
        _jobStore.Set(CreateJob("job-import-retention", ExecutionJobStatus.Succeeded, completedAt.AddMinutes(-1), "corr-import-retention") with
        {
            Spec = CreateSpec("import") with { Kind = ExecutionJobKind.FileImport },
            CompletedAt = completedAt,
            ArtifactReferences = ["https://example.test/results/output.json"]
        });

        using var response = await _client.GetAsync("/api/v1/admin/jobs/job-import-retention/diagnostics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var diagnostic = await ReadJsonAsync(response);
        var artifacts = diagnostic.RootElement.GetProperty("artifacts");
        artifacts.GetProperty("state").GetString().Should().Be("complete");
        artifacts.GetProperty("expiredCount").GetInt32().Should().Be(0);
    }

    private sealed class RetentionTestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
