// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;
using Honua.Server.Features.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Locks the remaining contract-first stub tools to <c>not_implemented</c>
/// output with a machine-readable <c>blockedBy</c> pointer to the upstream
/// service so operators have an unblock path while the planner rolls out.
/// Stubs still enforce authentication and emit structured output. The grounder
/// and clarifier have shipped and are covered by their own delegation tests.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpStubToolTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysisStub_ReturnsNotImplementedWithPlannerBlocker()
    {
        var tool = new PlanAnalysisTool(_jobService, NullLogger<PlanAnalysisTool>.Instance);

        var result = await InvokeAsync(tool);

        AssertNotImplemented(result, expectedTool: PlanAnalysisTool.ToolName, expectedBlocker: "honua.planner.service");
        _jobService.Received(1).EnsureCallerAuthorized(
            Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Process, OperatorOperation.Read);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysisStub_AuthenticatedButUnauthorized_ThrowsPermissionDenied()
    {
        _jobService
            .When(s => s.EnsureCallerAuthorized(
                Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Process, OperatorOperation.Read))
            .Do(_ => throw new GeoprocessingAuthorizationException(requiresAuthentication: false));
        var tool = new PlanAnalysisTool(_jobService, NullLogger<PlanAnalysisTool>.Instance);

        var act = async () => await InvokeAsync(tool);

        (await act.Should().ThrowAsync<GeoprocessingAuthorizationException>())
            .Which.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public void PlanAnalysisStub_ImplementsIStubMcpTool_ForTelemetryTagging()
    {
        IMcpTool plan = new PlanAnalysisTool(_jobService, NullLogger<PlanAnalysisTool>.Instance);

        plan.Should().BeAssignableTo<IStubMcpTool>();
    }

    [UnitTest]
    public void PlanAnalysisStub_DescriptorExposesEmptyObjectSchemaForContract()
    {
        var descriptor = new PlanAnalysisTool(_jobService, NullLogger<PlanAnalysisTool>.Instance).Describe();

        descriptor.Name.Should().StartWith("honua_");
        descriptor.Description.Should().NotBeNullOrWhiteSpace();
        descriptor.InputSchema.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysisStub_RejectsArrayArguments_AsInvalidArgument()
    {
        var tool = new PlanAnalysisTool(_jobService, NullLogger<PlanAnalysisTool>.Instance);

        var invoke = async () => await InvokeWithAsync(tool, """["not","an","object"]""");

        await invoke.Should()
            .ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*object*");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysisStub_AcceptsMissingArguments_AsEmptyObject()
    {
        var tool = new PlanAnalysisTool(_jobService, NullLogger<PlanAnalysisTool>.Instance);

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            arguments: null,
            CancellationToken.None);

        AssertNotImplemented(result, expectedTool: PlanAnalysisTool.ToolName, expectedBlocker: "honua.planner.service");
    }

    private static async Task<McpToolsCallResult> InvokeAsync(PlanAnalysisTool tool)
    {
        JsonElement? arguments = McpTestFactory.ParseJson("{}");
        return await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);
    }

    private static async Task<McpToolsCallResult> InvokeWithAsync(PlanAnalysisTool tool, string argumentsJson)
    {
        JsonElement? arguments = McpTestFactory.ParseJson(argumentsJson);
        return await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);
    }

    private static void AssertNotImplemented(
        McpToolsCallResult result,
        string expectedTool,
        string expectedBlocker)
    {
        result.IsError.Should().BeFalse();
        result.StructuredContent.Should().NotBeNull();
        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("not_implemented");
        body.GetProperty("tool").GetString().Should().Be(expectedTool);
        body.GetProperty("blockedBy").GetString().Should().Be(expectedBlocker);
        body.GetProperty("contract").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("nextSteps").ValueKind.Should().Be(JsonValueKind.Array);
    }
}
