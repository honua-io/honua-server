// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.OgcProcesses;

/// <summary>
/// Verifies that OGC API Processes job routes reject jobs whose
/// <see cref="ExecutionJobKind"/> is not <see cref="ExecutionJobKind.Geoprocessing"/>.
/// Non-geoprocessing jobs must return 404 (no-such-job) so the adapter
/// never exposes or mislabels jobs from other execution slices.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.OgcApiProcesses)]
public sealed class OgcProcessesJobKindFilterTests : IAsyncLifetime
{
    private const string EtlJobId = "etl-job-001";
    private const string GeoJobId = "geo-job-001";

    private static readonly ExecutionJobRecord EtlJob = new()
    {
        OperationId = EtlJobId,
        Status = ExecutionJobStatus.Running,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "test-backend",
            Kind = ExecutionJobKind.ExtractTransformLoad,
            WorkloadName = "etl-workload"
        }
    };

    private static readonly ExecutionJobRecord GeoJob = new()
    {
        OperationId = GeoJobId,
        Status = ExecutionJobStatus.Running,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "test-backend",
            Kind = ExecutionJobKind.Geoprocessing,
            WorkloadName = "geo-workload"
        }
    };

    private readonly IExecutionJobStore _mockJobStore;
    private readonly WebAppFixture _fixture;

    public OgcProcessesJobKindFilterTests()
    {
        _mockJobStore = Substitute.For<IExecutionJobStore>();

        _mockJobStore.GetAsync(EtlJobId, Arg.Any<CancellationToken>())
            .Returns(EtlJob);
        _mockJobStore.GetAsync(GeoJobId, Arg.Any<CancellationToken>())
            .Returns(GeoJob);
        _mockJobStore.GetAsync(Arg.Is<string>(id => id != EtlJobId && id != GeoJobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        _mockJobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExecutionJobRecord> { GeoJob });

        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_mockJobStore);
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    public async Task JobStatus_NonGeoprocessingJob_Returns404()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{EtlJobId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should().Contain("no-such-job");
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    public async Task JobStatus_GeoprocessingJob_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{GeoJobId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("jobID").GetString().Should().Be(GeoJobId);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_NonGeoprocessingJob_Returns404()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/processes/jobs/{EtlJobId}/results");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should().Contain("no-such-job");
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_NonGeoprocessingJob_Returns404()
    {
        var response = await _fixture.Client.DeleteAsync($"/ogc/processes/jobs/{EtlJobId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should().Contain("no-such-job");
    }
}
