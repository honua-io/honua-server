// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.Ogc.Api.Processes;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

[Protocol(TestProtocols.OgcApiProcesses)]
[global::Xunit.Trait("Tier", "Fast")]
public sealed class OgcProcessesJobListTests
{
    [UnitTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs")]
    public async Task GetJobList_IncludesTerminalStatusesAndFiltersNonGeoprocessingJobs()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "operator")], "test"));
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorization.EvaluateAsync(principal, Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));
        var approvals = Substitute.For<IOperatorApprovalEvaluator>();
        approvals.Evaluate(principal, Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton(new OperatorApprovalGate(authorization, approvals, NullLogger<OperatorApprovalGate>.Instance))
            .BuildServiceProvider();

        GeoprocessingJobListFilter? capturedFilter = null;
        var gpSucceeded = CreateJob("job-gp-succeeded", ExecutionJobKind.Geoprocessing, ExecutionJobStatus.Succeeded);
        var gpFailed = CreateJob("job-gp-failed", ExecutionJobKind.Geoprocessing, ExecutionJobStatus.Failed);
        var nonGp = CreateJob("job-other-kind", ExecutionJobKind.TileCache, ExecutionJobStatus.Succeeded);

        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.ListJobsAsync(Arg.Any<GeoprocessingJobListFilter>(), principal, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedFilter = call.Arg<GeoprocessingJobListFilter>();
                return Task.FromResult(new GeoprocessingJobListPage
                {
                    Items = [gpSucceeded, nonGp, gpFailed],
                });
            });

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = principal,
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.com");
        context.Response.Body = new MemoryStream();

        var result = await JobEndpoints.GetJobList(
            context,
            NullLogger<OgcProcessesEndpointsLog>.Instance,
            Options.Create(new OgcProcessesOptions()),
            jobService);
        await result.ExecuteAsync(context);

        capturedFilter.Should().NotBeNull();
        capturedFilter!.Statuses.Should().Contain(ExecutionJobStatus.Queued);
        capturedFilter.Statuses.Should().Contain(ExecutionJobStatus.Provisioning);
        capturedFilter.Statuses.Should().Contain(ExecutionJobStatus.Running);
        capturedFilter.Statuses.Should().Contain(ExecutionJobStatus.Succeeded);
        capturedFilter.Statuses.Should().Contain(ExecutionJobStatus.Failed);
        capturedFilter.Statuses.Should().Contain(ExecutionJobStatus.Cancelled);

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var jobIds = doc.RootElement.GetProperty("jobs")
            .EnumerateArray()
            .Select(item => item.GetProperty("jobID").GetString())
            .ToArray();
        jobIds.Should().Equal("job-gp-succeeded", "job-gp-failed");
    }

    private static ExecutionJobRecord CreateJob(string id, ExecutionJobKind kind, ExecutionJobStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = id,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = kind,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["protocolProcessId"] = "geometry.buffer",
                },
            },
        };
    }
}
