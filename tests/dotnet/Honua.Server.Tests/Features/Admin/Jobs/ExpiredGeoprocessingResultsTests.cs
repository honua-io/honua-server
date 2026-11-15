// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
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
    [IntegrationTheory]
    [InlineData(2, HttpStatusCode.NotFound, 0)]
    [InlineData(0, HttpStatusCode.ServiceUnavailable, 1)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task GetJobResults_ReplayStoreUnavailable_RespectsResultRetention(
        int completedHoursAgo, HttpStatusCode expectedStatus, int expectedReplayCount)
    {
        var job = CreateJob(completedHoursAgo);
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        var packages = Substitute.For<IGeoprocessingResultPackageStore>();
        var callback = new UnavailableReplayCallback();
        var fixture = CreateFixture(jobs, packages, callback);

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync($"/ogc/processes/jobs/{job.OperationId}/results");

            response.StatusCode.Should().Be(expectedStatus, await response.Content.ReadAsStringAsync());
            callback.ReplayCount.Should().Be(expectedReplayCount);
            await packages.Received(expectedReplayCount).GetAsync(job.OperationId, Arg.Any<CancellationToken>());
            await packages.DidNotReceive().SetAsync(
                Arg.Any<string>(), Arg.Any<AnalysisResultPackage>(),
                Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixture(
        IExecutionJobStore jobs,
        IGeoprocessingResultPackageStore packages,
        IJobTerminalCallback callback)
        => new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IExecutionJobStore>();
            services.AddSingleton(jobs);
            services.RemoveAll<IGeoprocessingResultPackageStore>();
            services.AddSingleton(packages);
            services.RemoveAll<IJobTerminalCallback>();
            services.AddSingleton<IJobTerminalCallback>(callback);
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

    private sealed class UnavailableReplayCallback : IReplayableJobTerminalCallback
    {
        public int ReplayCount { get; private set; }

        public ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask ReplayAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
        {
            ReplayCount++;
            throw new GeoprocessingStoreUnavailableException("Result persistence is temporarily unavailable.");
        }
    }
}
