// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Horizontal-authorization coverage for OGC API Processes job reads.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesJobOwnershipTests : IClassFixture<OgcProcessesJobOwnershipTestsFixture>
{
    private readonly HttpClient _client;

    public OgcProcessesJobOwnershipTests(OgcProcessesJobOwnershipTestsFixture fixture)
        => _client = fixture.Client;

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    public async Task JobStatus_CrossOwnerJob_ReturnsNotFound()
    {
        using var response = await _client.GetAsync("/ogc/processes/jobs/other-job");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    public async Task JobStatus_OwnerlessJob_ReturnsNotFound()
    {
        using var response = await _client.GetAsync("/ogc/processes/jobs/ownerless-job");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs")]
    public async Task JobList_MixedOwnership_ReturnsOnlyCallerJobs()
    {
        using var response = await _client.GetAsync("/ogc/processes/jobs?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobIds = document.RootElement.GetProperty("jobs")
            .EnumerateArray()
            .Select(job => job.GetProperty("jobID").GetString())
            .ToArray();
        jobIds.Should().Equal("owned-job");
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_CrossOwnerFailedJob_ReturnsNotFoundWithoutErrorLeak()
    {
        using var response = await _client.GetAsync("/ogc/processes/jobs/other-failed-job/results");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("private failure detail");
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_CrossOwnerSucceededJob_ReturnsNotFound()
    {
        using var response = await _client.GetAsync("/ogc/processes/jobs/other-succeeded-job/results");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// Authenticated OGC host seeded with caller-owned, cross-owner, and ownerless jobs.
/// </summary>
public sealed class OgcProcessesJobOwnershipTestsFixture : IAsyncLifetime
{
    private readonly InMemoryExecutionJobStore _jobStore = new();
    private readonly WebAppFixture _app;

    public OgcProcessesJobOwnershipTestsFixture()
    {
        var authorizer = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorizer.EvaluateAsync(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<OperatorAuthorizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Allowed());
        var approval = Substitute.For<IOperatorApprovalEvaluator>();
        approval.Evaluate(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        _app = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IExecutionJobStore>();
                    services.AddSingleton<IExecutionJobStore>(_jobStore);
                    services.RemoveAll<IOperatorAuthorizationEvaluator>();
                    services.AddSingleton(authorizer);
                    services.RemoveAll<IOperatorApprovalEvaluator>();
                    services.AddSingleton(approval);
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                    services.PostConfigureAll<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                    });
                });
            });
    }

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _jobStore.TryCreateAsync(CreateJob("owned-job", "alice"));
        await _jobStore.TryCreateAsync(CreateJob("other-job", "bob"));
        await _jobStore.TryCreateAsync(CreateJob("ownerless-job", null));
        await _jobStore.TryCreateAsync(CreateJob(
            "other-failed-job",
            "bob",
            ExecutionJobStatus.Failed,
            "private failure detail"));
        await _jobStore.TryCreateAsync(CreateJob(
            "other-succeeded-job",
            "bob",
            ExecutionJobStatus.Succeeded));
        await _app.InitializeAsync();
        Client = _app.Client;
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "alice");
    }

    public Task DisposeAsync() => _app.DisposeAsync();

    private static ExecutionJobRecord CreateJob(
        string jobId,
        string? owner,
        ExecutionJobStatus status = ExecutionJobStatus.Running,
        string? errorMessage = null)
        => new()
        {
            OperationId = jobId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ErrorMessage = errorMessage,
            Audit = new OperationAuditInfo { RequestedBy = owner },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "ownership-test"
            }
        };
}
