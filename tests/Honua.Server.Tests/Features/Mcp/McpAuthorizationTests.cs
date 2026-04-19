// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp;
using Honua.Server.Features.Mcp.Models;
using Honua.Server.Features.Mcp.Resources;
using Honua.Server.Features.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Enforces that every MCP tool and resource terminates unauthenticated
/// requests with <see cref="GeoprocessingAuthorizationException"/> before any
/// domain delegation. These tests guard the authentication gate that
/// <see cref="McpErrorMapper"/> translates into the <c>unauthenticated</c>
/// error envelope for operators.
/// </summary>
[Protocol(Protocols.Mcp)]
public sealed class McpAuthorizationTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_validate_plan")]
    public async Task ValidatePlan_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var tool = new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpPlanArgument { Plan = McpTestFactory.CreateValidPlanInput() },
            McpJsonContext.Default.McpPlanArgument);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AnonymousHttpContext(), arguments, CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingAuthorizationException>())
            .Which.RequiresAuthentication.Should().BeTrue();
        _jobService.DidNotReceiveWithAnyArgs().ValidatePlan(default!, default!);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_dry_run_plan")]
    public async Task DryRunPlan_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var tool = new DryRunPlanTool(_jobService, NullLogger<DryRunPlanTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpPlanArgument { Plan = McpTestFactory.CreateValidPlanInput() },
            McpJsonContext.Default.McpPlanArgument);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AnonymousHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        _jobService.DidNotReceiveWithAnyArgs().DryRunPlan(default!, default!);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_execute_plan")]
    public async Task ExecutePlan_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var tool = new ExecutePlanTool(_jobService, NullLogger<ExecutePlanTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpExecutePlanArgument { Plan = McpTestFactory.CreateValidPlanInput() },
            McpJsonContext.Default.McpExecutePlanArgument);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AnonymousHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await _jobService.DidNotReceiveWithAnyArgs().SubmitJobAsync(
            default!, default, default!, default, default);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_cancel_job")]
    public async Task CancelJob_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var tool = new CancelJobTool(_jobService, NullLogger<CancelJobTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpCancelJobArgument { JobId = "job-1" },
            McpJsonContext.Default.McpCancelJobArgument);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AnonymousHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await _jobService.DidNotReceiveWithAnyArgs().CancelJobAsync(
            default!, default!, default);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysisStub_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var tool = new PlanAnalysisTool(_jobService, NullLogger<PlanAnalysisTool>.Instance);

        JsonElement? arguments = McpTestFactory.ParseJson("{}");
        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AnonymousHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidatesStub_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var tool = new GroundCandidatesTool(_jobService, NullLogger<GroundCandidatesTool>.Instance);

        JsonElement? arguments = McpTestFactory.ParseJson("{}");
        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AnonymousHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_clarify_intent")]
    public async Task ClarifyIntentStub_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var tool = new ClarifyIntentTool(_jobService, NullLogger<ClarifyIntentTool>.Instance);

        JsonElement? arguments = McpTestFactory.ParseJson("{}");
        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AnonymousHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://jobs/{jobId}")]
    public async Task JobStatusResource_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var resource = new JobStatusResource(_jobService, NullLogger<JobStatusResource>.Instance);

        var act = async () => await resource.ReadAsync(
            McpTestFactory.AnonymousHttpContext(), "honua://jobs/job-1", CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await _jobService.DidNotReceiveWithAnyArgs().GetJobAsync(default!, default!, default);
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://jobs/{jobId}/results")]
    public async Task JobResultsResource_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var resource = new JobResultsResource(_jobService, NullLogger<JobResultsResource>.Instance);

        var act = async () => await resource.ReadAsync(
            McpTestFactory.AnonymousHttpContext(), "honua://jobs/job-1/results", CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await _jobService.DidNotReceiveWithAnyArgs().GetJobResultsAsync(default!, default!, default);
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://workspaces/{workspaceId}")]
    public async Task WorkspaceResource_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var resource = new WorkspaceResource(_jobService, NullLogger<WorkspaceResource>.Instance);

        var act = async () => await resource.ReadAsync(
            McpTestFactory.AnonymousHttpContext(), "honua://workspaces/ws-1", CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://catalog/processes")]
    public async Task ProcessCatalogResource_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var resource = new ProcessCatalogResource(_jobService, NullLogger<ProcessCatalogResource>.Instance);

        var act = async () => await resource.ReadAsync(
            McpTestFactory.AnonymousHttpContext(), "honua://catalog/processes", CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://workspaces/{workspaceId}")]
    public async Task WorkspaceResource_AuthenticatedButUnauthorized_ThrowsPermissionDenied()
    {
        _jobService
            .When(s => s.EnsureCallerAuthorized(
                Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Workspace, OperatorOperation.Read))
            .Do(_ => throw new GeoprocessingAuthorizationException(requiresAuthentication: false));
        var resource = new WorkspaceResource(_jobService, NullLogger<WorkspaceResource>.Instance);

        var act = async () => await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(), "honua://workspaces/ws-1", CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingAuthorizationException>())
            .Which.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://catalog/processes")]
    public async Task ProcessCatalogResource_AuthenticatedButUnauthorized_ThrowsPermissionDenied()
    {
        _jobService
            .When(s => s.EnsureCallerAuthorized(
                Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Catalog, OperatorOperation.Discover))
            .Do(_ => throw new GeoprocessingAuthorizationException(requiresAuthentication: false));
        var resource = new ProcessCatalogResource(_jobService, NullLogger<ProcessCatalogResource>.Instance);

        var act = async () => await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(), "honua://catalog/processes", CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingAuthorizationException>())
            .Which.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public void StubResources_ImplementIStubMcpResource_ForTelemetryTagging()
    {
        IMcpResource workspace = new WorkspaceResource(_jobService, NullLogger<WorkspaceResource>.Instance);
        IMcpResource catalog = new ProcessCatalogResource(_jobService, NullLogger<ProcessCatalogResource>.Instance);

        workspace.Should().BeAssignableTo<IStubMcpResource>();
        catalog.Should().BeAssignableTo<IStubMcpResource>();
    }
}
