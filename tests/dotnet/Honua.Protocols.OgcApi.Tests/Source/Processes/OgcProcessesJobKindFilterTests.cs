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
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Verifies that OGC API Processes job routes reject jobs whose
/// <see cref="ExecutionJobKind"/> is not <see cref="ExecutionJobKind.Geoprocessing"/>.
/// Non-geoprocessing jobs must return 404 (no-such-job) so the adapter
/// never exposes or mislabels jobs from other execution slices.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesJobKindFilterTests : IClassFixture<OgcProcessesJobKindFilterTestsFixture>
{
    private const string EtlJobId = "etl-job-001";
    private const string GeoJobId = "geo-job-001";

    private readonly WebAppFixture _fixture;

    public OgcProcessesJobKindFilterTests(OgcProcessesJobKindFilterTestsFixture fixture) => _fixture = fixture.App;

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

/// <summary>
/// Per-class wrapper that owns a single <see cref="WebAppFixture"/> with the
/// non-geoprocessing/geoprocessing job store substitute registered once, shared
/// across all tests in <see cref="OgcProcessesJobKindFilterTests"/> via
/// <see cref="IClassFixture{T}"/>. All mock setups are fixed (no per-test
/// reconfiguration) and every test only reads fixed job ids, so the shared
/// fixture is safe.
/// </summary>
public sealed class OgcProcessesJobKindFilterTestsFixture : IAsyncLifetime
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

    public WebAppFixture App { get; }

    public OgcProcessesJobKindFilterTestsFixture()
    {
        var mockJobStore = Substitute.For<IExecutionJobStore>().WithTrySet();

        mockJobStore.GetAsync(EtlJobId, Arg.Any<CancellationToken>())
            .Returns(EtlJob);
        mockJobStore.GetAsync(GeoJobId, Arg.Any<CancellationToken>())
            .Returns(GeoJob);
        mockJobStore.GetAsync(Arg.Is<string>(id => id != EtlJobId && id != GeoJobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        mockJobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExecutionJobRecord> { GeoJob });

        App = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.AddSingleton(mockJobStore);
            });
    }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();
}
