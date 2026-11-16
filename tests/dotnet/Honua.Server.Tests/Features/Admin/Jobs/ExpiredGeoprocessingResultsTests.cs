// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin.Jobs;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiProcesses)]
[Operation(Operations.JobResults)]
public sealed class ExpiredGeoprocessingResultsTests
{
    // The real terminal callback replays against a result package store whose writes fail, which
    // is the retryable 503 a reader sees while results are retained. Once retention has passed the
    // request must answer 404 without reading or writing the store at all.
    [IntegrationTheory]
    [InlineData(2, HttpStatusCode.NotFound, 0)]
    [InlineData(0, HttpStatusCode.ServiceUnavailable, 1)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task GetJobResults_ReplayStoreUnavailable_RespectsResultRetention(
        int completedHoursAgo, HttpStatusCode expectedStatus, int expectedStoreCalls)
    {
        var job = CreateJob(completedHoursAgo);
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        var packages = Substitute.For<IGeoprocessingResultPackageStore>();
        packages.SetAsync(
                Arg.Any<string>(), Arg.Any<AnalysisResultPackage>(),
                Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Result persistence is temporarily unavailable.")));
        var fixture = CreateFixture(jobs, packages);

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync($"/ogc/processes/jobs/{job.OperationId}/results");

            var body = await response.Content.ReadAsStringAsync();

            using var scope = new AssertionScope();
            response.StatusCode.Should().Be(expectedStatus, body);
            packages.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(packages.GetAsync))
                .Should().Be(expectedStoreCalls, "the store is read only while results are retained");
            packages.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(packages.SetAsync))
                .Should().Be(expectedStoreCalls, "the replay writes only while results are retained");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixture(IExecutionJobStore jobs, IGeoprocessingResultPackageStore packages)
        => new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IExecutionJobStore>();
            services.AddSingleton(jobs);
            services.RemoveAll<IGeoprocessingResultPackageStore>();
            services.AddSingleton(packages);
            // The worker registers this callback only when a job queue is present; the read path
            // under test needs it whatever the fixture's worker wiring is.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IJobTerminalCallback, GeoprocessingJobTerminalCallback>());
            services.PostConfigure<TenantContextOptions>(options => options.DefaultTenantId = "public");
            services.PostConfigure<GeoprocessingExecutorOptions>(options =>
                options.ResultRetention = TimeSpan.FromHours(1));
        });

    private static ExecutionJobRecord CreateJob(int completedHoursAgo)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = "result-replay-retention",
            Status = ExecutionJobStatus.Succeeded,
            Version = 1,
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now,
            CompletedAt = now.AddHours(-completedHoursAgo),
            Audit = new OperationAuditInfo
            {
                RequestedBy = "retention-test",
                SubmitterSecurityContext = new JobSecurityContext("retention-test", "public", [])
                {
                    OwnerActorId = "retention-test"
                }
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "result-replay-retention"
            }
        };
    }
}
