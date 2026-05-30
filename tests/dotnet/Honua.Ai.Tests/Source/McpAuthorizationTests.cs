// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Enforces that every MCP tool and resource terminates unauthenticated
/// requests with <see cref="GeoprocessingAuthorizationException"/> before any
/// domain delegation. These tests guard the authentication gate that
/// <see cref="McpErrorMapper"/> translates into the <c>unauthenticated</c>
/// error envelope for operators.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpAuthorizationTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();
    private readonly IGroundingService _groundingService = Substitute.For<IGroundingService>();

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
    public async Task PlanAnalysis_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var tool = new PlanAnalysisTool(
            Substitute.For<Honua.Ai.AiBuilder.Planning.IPlanAnalysisService>(),
            _jobService,
            NullLogger<PlanAnalysisTool>.Instance);

        JsonElement? arguments = McpTestFactory.ParseJson("""{"intent":"build a dashboard"}""");
        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AnonymousHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);

        JsonElement? arguments = McpTestFactory.ParseJson("""{"goal":"Buffer the parcels"}""");
        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AnonymousHttpContext(), arguments, CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingAuthorizationException>())
            .Which.RequiresAuthentication.Should().BeTrue();
        await _groundingService.DidNotReceiveWithAnyArgs().GroundAsync(default!, default!, default);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_clarify_intent")]
    public async Task ClarifyIntent_WithoutAuthenticatedPrincipal_ThrowsAuthenticationRequired()
    {
        var tool = new ClarifyIntentTool(_groundingService, _jobService, NullLogger<ClarifyIntentTool>.Instance);

        JsonElement? arguments = McpTestFactory.ParseJson("""
            {"intentId":"intent-1","goal":"Buffer the parcels","response":{"answers":{"q1":["a"]}}}
            """);
        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AnonymousHttpContext(), arguments, CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingAuthorizationException>())
            .Which.RequiresAuthentication.Should().BeTrue();
        await _groundingService.DidNotReceiveWithAnyArgs().GroundAsync(default!, default!, default);
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
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidates_AuthenticatedButUnauthorized_ThrowsPermissionDenied()
    {
        _jobService
            .When(s => s.EnsureCallerAuthorized(
                Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Catalog, OperatorOperation.Discover))
            .Do(_ => throw new GeoprocessingAuthorizationException(requiresAuthentication: false));
        var tool = new GroundCandidatesTool(_groundingService, _jobService, NullLogger<GroundCandidatesTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""{"goal":"Buffer the parcels"}""");

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingAuthorizationException>())
            .Which.RequiresAuthentication.Should().BeFalse();
        await _groundingService.DidNotReceiveWithAnyArgs().GroundAsync(default!, default!, default);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_clarify_intent")]
    public async Task ClarifyIntent_AuthenticatedButUnauthorized_ThrowsPermissionDenied()
    {
        // honua_clarify_intent must gate on the same (Catalog, Discover) pair
        // as honua_ground_candidates — both tools delegate to
        // IGroundingService.GroundAsync, so asymmetric permissions would let a
        // caller start grounding but fail to answer its clarification envelope.
        _jobService
            .When(s => s.EnsureCallerAuthorized(
                Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Catalog, OperatorOperation.Discover))
            .Do(_ => throw new GeoprocessingAuthorizationException(requiresAuthentication: false));
        var tool = new ClarifyIntentTool(_groundingService, _jobService, NullLogger<ClarifyIntentTool>.Instance);
        JsonElement? arguments = McpTestFactory.ParseJson("""
            {"intentId":"intent-1","goal":"Buffer the parcels","response":{"answers":{"q1":["a"]}}}
            """);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingAuthorizationException>())
            .Which.RequiresAuthentication.Should().BeFalse();
        await _groundingService.DidNotReceiveWithAnyArgs().GroundAsync(default!, default!, default);
    }

    [UnitTest]
    public void CoreInspectionResources_AreFunctionalResources_ForTelemetryTagging()
    {
        IMcpResource workspace = new WorkspaceResource(_jobService, NullLogger<WorkspaceResource>.Instance);
        IMcpResource catalog = new ProcessCatalogResource(_jobService, NullLogger<ProcessCatalogResource>.Instance);

        workspace.Should().NotBeAssignableTo<IStubMcpResource>();
        catalog.Should().NotBeAssignableTo<IStubMcpResource>();
    }

    // The dispatcher-level auth gate runs before param parsing and tool/resource
    // lookup, so malformed payloads and unknown names from anonymous callers
    // still surface the `unauthenticated` reauthentication signal instead of
    // leaking the protocol-level `invalid_argument`/`not_found` codes.
    [UnitTest]
    [Endpoint("POST /mcp tools/call")]
    public async Task DispatchAsync_AnonymousToolsCall_WithUnknownTool_ReturnsUnauthenticatedIsError()
    {
        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"u-1\""),
            Method = "tools/call",
            Params = McpTestFactory.ParseJson("""{"name":"honua_does_not_exist","arguments":{}}""")
        };

        var response = await surface.DispatchAsync(
            McpTestFactory.AnonymousHttpContext(), request, CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString()
            .Should().Be(McpErrorMapper.Codes.Unauthenticated);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call")]
    public async Task DispatchAsync_AnonymousToolsCall_WithMalformedParams_ReturnsUnauthenticatedIsError()
    {
        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"u-2\""),
            Method = "tools/call",
            Params = McpTestFactory.ParseJson("[\"not\",\"an\",\"object\"]")
        };

        var response = await surface.DispatchAsync(
            McpTestFactory.AnonymousHttpContext(), request, CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString()
            .Should().Be(McpErrorMapper.Codes.Unauthenticated);
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read")]
    public async Task DispatchAsync_AnonymousResourcesRead_WithUnknownUri_ReturnsUnauthenticatedJsonRpcError()
    {
        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"u-3\""),
            Method = "resources/read",
            Params = McpTestFactory.ParseJson("""{"uri":"honua://unknown/thing"}""")
        };

        var response = await surface.DispatchAsync(
            McpTestFactory.AnonymousHttpContext(), request, CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().NotBeNull();
        response.Error!.Data!.Code.Should().Be(McpErrorMapper.Codes.Unauthenticated);
        response.Error.Data.RequiresReauthentication.Should().BeTrue();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read")]
    public async Task DispatchAsync_AnonymousResourcesRead_WithMalformedParams_ReturnsUnauthenticatedJsonRpcError()
    {
        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"u-4\""),
            Method = "resources/read",
            Params = McpTestFactory.ParseJson("\"not-an-object\"")
        };

        var response = await surface.DispatchAsync(
            McpTestFactory.AnonymousHttpContext(), request, CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().NotBeNull();
        response.Error!.Data!.Code.Should().Be(McpErrorMapper.Codes.Unauthenticated);
    }

    private McpOperatorSurface BuildSurface()
    {
        var tools = new IMcpTool[]
        {
            new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance),
            new DryRunPlanTool(_jobService, NullLogger<DryRunPlanTool>.Instance),
            new ExecutePlanTool(_jobService, NullLogger<ExecutePlanTool>.Instance),
            new CancelJobTool(_jobService, NullLogger<CancelJobTool>.Instance)
        };
        var resources = new IMcpResource[]
        {
            new JobStatusResource(_jobService, NullLogger<JobStatusResource>.Instance),
            new JobResultsResource(_jobService, NullLogger<JobResultsResource>.Instance),
            new WorkspaceResource(_jobService, NullLogger<WorkspaceResource>.Instance),
            new ProcessCatalogResource(_jobService, NullLogger<ProcessCatalogResource>.Instance)
        };
        return new McpOperatorSurface(tools, resources, NullLogger<McpOperatorSurface>.Instance);
    }
}
