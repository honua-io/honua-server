// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;
using Honua.Server.Features.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Locks the contract-first stub tools to <c>not_implemented</c> output with
/// a machine-readable <c>blockedBy</c> pointer to the upstream service so
/// operators have an unblock path while the planner/grounder/clarifier roll
/// out. Stubs still enforce authentication and emit structured output.
/// </summary>
[Protocol(Protocols.Mcp)]
public sealed class McpStubToolTests
{
    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysisStub_ReturnsNotImplementedWithPlannerBlocker()
    {
        var tool = new PlanAnalysisTool(NullLogger<PlanAnalysisTool>.Instance);

        var result = await InvokeAsync(tool);

        AssertNotImplemented(result, expectedTool: PlanAnalysisTool.ToolName, expectedBlocker: "honua.planner.service");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task GroundCandidatesStub_ReturnsNotImplementedWithGroundingBlocker()
    {
        var tool = new GroundCandidatesTool(NullLogger<GroundCandidatesTool>.Instance);

        var result = await InvokeAsync(tool);

        AssertNotImplemented(result, expectedTool: GroundCandidatesTool.ToolName, expectedBlocker: "honua.grounding.service");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_clarify_intent")]
    public async Task ClarifyIntentStub_ReturnsNotImplementedWithClarifierBlocker()
    {
        var tool = new ClarifyIntentTool(NullLogger<ClarifyIntentTool>.Instance);

        var result = await InvokeAsync(tool);

        AssertNotImplemented(result, expectedTool: ClarifyIntentTool.ToolName, expectedBlocker: "honua.clarifier.service");
    }

    [UnitTest]
    public void StubDescriptors_ExposeEmptyObjectSchemaForContract()
    {
        var plan = new PlanAnalysisTool(NullLogger<PlanAnalysisTool>.Instance).Describe();
        var ground = new GroundCandidatesTool(NullLogger<GroundCandidatesTool>.Instance).Describe();
        var clarify = new ClarifyIntentTool(NullLogger<ClarifyIntentTool>.Instance).Describe();

        foreach (var descriptor in new[] { plan, ground, clarify })
        {
            descriptor.Name.Should().StartWith("honua_");
            descriptor.Description.Should().NotBeNullOrWhiteSpace();
            descriptor.InputSchema.ValueKind.Should().Be(JsonValueKind.Object);
        }
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task Stub_RejectsArrayArguments_AsInvalidArgument()
    {
        var tool = new PlanAnalysisTool(NullLogger<PlanAnalysisTool>.Instance);

        var invoke = async () => await InvokeWithAsync(tool, """["not","an","object"]""");

        await invoke.Should()
            .ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*object*");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_ground_candidates")]
    public async Task Stub_RejectsScalarArguments_AsInvalidArgument()
    {
        var tool = new GroundCandidatesTool(NullLogger<GroundCandidatesTool>.Instance);

        var invoke = async () => await InvokeWithAsync(tool, "\"just-a-string\"");

        await invoke.Should()
            .ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*object*");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_clarify_intent")]
    public async Task Stub_RejectsObjectWithUnexpectedProperties_AsInvalidArgument()
    {
        var tool = new ClarifyIntentTool(NullLogger<ClarifyIntentTool>.Instance);

        var invoke = async () => await InvokeWithAsync(tool, """{"unexpected":"value"}""");

        await invoke.Should()
            .ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*unexpected*");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task Stub_AcceptsMissingArguments_AsEmptyObject()
    {
        var tool = new PlanAnalysisTool(NullLogger<PlanAnalysisTool>.Instance);

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            arguments: null,
            CancellationToken.None);

        AssertNotImplemented(result, expectedTool: PlanAnalysisTool.ToolName, expectedBlocker: "honua.planner.service");
    }

    private static async Task<McpToolsCallResult> InvokeAsync(IMcpTool tool)
    {
        JsonElement? arguments = McpTestFactory.ParseJson("{}");
        return await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);
    }

    private static async Task<McpToolsCallResult> InvokeWithAsync(IMcpTool tool, string argumentsJson)
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
