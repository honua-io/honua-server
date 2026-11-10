// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.CustomCode;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin.Jobs;

public sealed partial class ConsoleJobEndpointsTests
{
    [IntegrationTheory]
    [InlineData(-1, false, ExecutionJobStatus.Succeeded, true)]
    [InlineData(0, true, ExecutionJobStatus.Succeeded, true)]
    [InlineData(1, true, ExecutionJobStatus.Succeeded, true)]
    [InlineData(0, true, ExecutionJobStatus.Failed, true)]
    [InlineData(0, true, ExecutionJobStatus.Cancelled, true)]
    [InlineData(0, true, ExecutionJobStatus.Succeeded, false)]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/artifacts")]
    public async Task GetJobDiagnostics_ResultRetentionBoundary_AgreesWithArtifactListing(long offsetTicks, bool expired, ExecutionJobStatus status, bool hasCompletionTime)
    {
        var completedAt = _retentionClock.GetUtcNow();
        var retention = _fixture.Services.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>().CurrentValue.ResultRetention;
        _retentionClock.UtcNow = completedAt + retention + TimeSpan.FromTicks(offsetTicks);
        _jobStore.Set(CreateJob("job-result-retention", status, completedAt.AddMinutes(-1), "corr-retention") with
        {
            CompletedAt = hasCompletionTime ? completedAt : null,
            UpdatedAt = hasCompletionTime ? _retentionClock.GetUtcNow() : completedAt,
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
            foreach (var item in items)
            {
                item.TryGetProperty("providerLink", out _).Should().BeFalse();
            }
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

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task GetJobDiagnostics_ClockCrossesExpiryDuringLookup_UsesOneAvailabilitySnapshot()
    {
        var retention = _fixture.Services.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>().CurrentValue.ResultRetention;
        var completedAt = _retentionClock.GetUtcNow() - retention + TimeSpan.FromTicks(1);
        _jobStore.Set(CreateJob("job-retention-snapshot", ExecutionJobStatus.Succeeded, completedAt.AddMinutes(-1), "corr-snapshot") with
        {
            CompletedAt = completedAt,
            ArtifactReferences = ["https://example.test/one", "https://example.test/two"]
        });
        _artifactStore.OnLookup = () => _retentionClock.UtcNow = completedAt + retention;
        using var response = await _client.GetAsync("/api/v1/admin/jobs/job-retention-snapshot/diagnostics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var diagnostic = await ReadJsonAsync(response);
        var artifacts = diagnostic.RootElement.GetProperty("artifacts");
        artifacts.GetProperty("state").GetString().Should().Be("complete");
        artifacts.GetProperty("availableCount").GetInt32().Should().Be(2);
        artifacts.GetProperty("expiredCount").GetInt32().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/artifacts")]
    public async Task GetArtifacts_PastGpRetention_PreservesCustomAndWorkspaceLifetimes()
    {
        var descriptor = CreateRetainedCustomArtifact();
        var fixture = CreateArtifactLifetimeFixture(descriptor);
        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync($"/api/v1/admin/jobs/{descriptor.JobId}/artifacts");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var listing = await ReadJsonAsync(response);
            var items = listing.RootElement.GetProperty("items");
            items[0].GetProperty("availability").GetString().Should().Be("Available");
            items[0].GetProperty("artifactId").GetString().Should().Be("custom:job-custom-retention:1:output.json");
            items[0].GetProperty("providerLink").GetString().Should().Be(descriptor.ContentUrl);
            items[1].GetProperty("availability").GetString().Should().Be("Available");
            items[1].GetProperty("artifactId").GetString().Should().Be("available");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static CustomCodeArtifactDescriptor CreateRetainedCustomArtifact()
    {
        return new CustomCodeArtifactDescriptor
        {
            JobId = "job-custom-retention",
            Attempt = 1,
            Name = "output.json",
            ObjectKey = "retained/output",
            Provider = CloudStorageProvider.AwsS3,
            State = "available",
            ContentType = "application/json",
            SizeBytes = 2,
            Sha256 = new string('a', 64)
        };
    }

    private WebAppFixture CreateArtifactLifetimeFixture(CustomCodeArtifactDescriptor descriptor)
    {
        var now = _retentionClock.GetUtcNow();
        _jobStore.Set(CreateJob(descriptor.JobId, ExecutionJobStatus.Succeeded, now.AddDays(-30), "corr-custom-retention") with
        {
            CompletedAt = now.AddDays(-29),
            ArtifactReferences = [descriptor.Serialize(), "available"]
        });
        var storage = CreateRetainedStorage(descriptor, now);
        return new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IExecutionJobStore>();
            services.AddSingleton<IExecutionJobStore>(_jobStore);
            services.RemoveAll<IArtifactStore>();
            services.AddSingleton<IArtifactStore>(_artifactStore);
            services.RemoveAll<ICloudFileStorage>();
            services.AddSingleton<ICloudFileStorage>(storage);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_retentionClock);
        });
    }

    private static ICloudFileStorage CreateRetainedStorage(CustomCodeArtifactDescriptor descriptor, DateTimeOffset now)
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.Provider.Returns(CloudStorageProvider.AwsS3);
        storage.GetMetadataAsync(descriptor.ObjectKey, Arg.Any<CancellationToken>()).Returns(new CloudFile
        {
            FileId = descriptor.ObjectKey,
            FileName = descriptor.Name,
            StoragePath = descriptor.ObjectKey,
            ContentType = descriptor.ContentType,
            SizeBytes = descriptor.SizeBytes,
            UploadedAt = now.AddDays(-30),
            ExpiresAt = now.AddDays(1),
            Provider = descriptor.Provider
        });
        return storage;
    }

    private sealed class RetentionTestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
