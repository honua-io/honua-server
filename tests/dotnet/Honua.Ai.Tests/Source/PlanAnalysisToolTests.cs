// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.AiBuilder.Fixtures;
using Honua.Server.Features.AiBuilder.Planning;
using Honua.Geoprocessing;
using Honua.Server.Features.Protocols.Mcp.Models;
using Honua.Server.Features.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class PlanAnalysisToolTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    private static PlanAnalysisTool CreateTool(IGeoprocessingJobService jobService) =>
        new(
            new FixturePlanAnalysisService(
                new AiBuilderFixtureCatalog(),
                NullLogger<FixturePlanAnalysisService>.Instance),
            jobService,
            NullLogger<PlanAnalysisTool>.Instance);

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_SpatialQuerySuccessPrompt_ReturnsPlanWithDagAndMutableSourceWarning()
    {
        var tool = CreateTool(_jobService);
        var arguments = McpTestFactory.ParseJson(
            """{"intent":"Show open hospitals within 1 km of flood zones as a linked map, table, and chart."}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("planned");
        body.GetProperty("contractVersion").GetString().Should().Be("honua.ai_builder.spatial_query.v1");
        body.GetProperty("fixtureCase").GetString().Should().Be("success");

        var plan = body.GetProperty("plan");
        plan.GetProperty("planId").GetString().Should().Be("plan-success-linked-dashboard");
        var stepIds = plan.GetProperty("steps")
            .EnumerateArray()
            .Select(s => s.GetProperty("stepId").GetString())
            .ToArray();
        stepIds.Should().BeEquivalentTo(["query-open-hospitals", "distance-filter", "compose-packages"]);

        var domainPlan = ToDomainPlan(plan);
        var (violations, _) = ProcessPlanValidator.Validate(domainPlan, new BuiltInProcessCatalog());
        violations.Should().BeEmpty();

        body.GetProperty("warnings").EnumerateArray()
            .Should().Contain(w => w.GetProperty("code").GetString() == "mutable_source_cache_warning");

        _jobService.Received(1).EnsureCallerAuthorized(
            Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Process, OperatorOperation.Read);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_AmbiguityPrompt_ReturnsClarificationRequiredWithCandidates()
    {
        var tool = CreateTool(_jobService);
        var arguments = McpTestFactory.ParseJson(
            """{"intent":"Find shelters near flood zones."}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("clarification_required");
        body.GetProperty("clarification").GetProperty("candidates").EnumerateArray()
            .Select(c => c.GetProperty("kind").GetString())
            .Should().Contain(["source", "unit", "operation"]);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_OversizedPrompt_ReturnsRejectedWithEstimate()
    {
        var tool = CreateTool(_jobService);
        var arguments = McpTestFactory.ParseJson(
            """{"intent":"Buffer every parcel in the county by 20 km and build a dashboard."}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("rejected");
        body.GetProperty("estimate").GetProperty("featureCount").GetInt64().Should().BeGreaterThan(0);
        body.GetProperty("estimate").GetProperty("limit").GetInt64().Should().BeGreaterThan(0);
        body.GetProperty("warnings").EnumerateArray()
            .Should().Contain(w => w.GetProperty("code").GetString() == "estimate_limit_exceeded");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_UnsupportedPrompt_ReturnsUnsupportedWithCapabilityState()
    {
        var tool = CreateTool(_jobService);
        var arguments = McpTestFactory.ParseJson(
            """{"intent":"Join every road segment to every flood polygon it crosses and summarize by route."}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("unsupported");
        var capabilityState = body.GetProperty("capabilityState");
        capabilityState.GetProperty("state").GetString().Should().Be("unsupported");
        capabilityState.GetProperty("name").GetString().Should().Be("spatialJoin");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_CacheHitPrompt_ReturnsPlannedWithCacheHitTrue()
    {
        var tool = CreateTool(_jobService);
        var arguments = McpTestFactory.ParseJson(
            """
            {
              "intent": "Show open hospitals within 1 km of flood zones as a linked map, table, and chart.",
              "context": {
                "fixtureCase": "cache-hit"
              }
            }
            """);

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("fixtureCase").GetString().Should().Be("cache-hit");
        var cache = body.GetProperty("cache");
        cache.GetProperty("key").GetString().Should().StartWith("sha256:");
        cache.GetProperty("hit").GetBoolean().Should().BeTrue();
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_MissingIntent_ThrowsValidationException()
    {
        var tool = CreateTool(_jobService);
        var arguments = McpTestFactory.ParseJson("""{"context":{}}""");

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*intent*");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_UnknownPrompt_ReturnsRejectedWithReason()
    {
        var tool = CreateTool(_jobService);
        var arguments = McpTestFactory.ParseJson(
            """{"intent":"some completely unmatched utterance"}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("rejected");
        body.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public void PlanAnalysis_Descriptor_AdvertisesIntentSchema()
    {
        var tool = CreateTool(_jobService);
        var descriptor = tool.Describe();

        descriptor.Name.Should().Be("honua_plan_analysis");
        descriptor.InputSchema.ValueKind.Should().Be(JsonValueKind.Object);
        descriptor.InputSchema.GetProperty("required").EnumerateArray()
            .Select(p => p.GetString())
            .Should().Contain("intent");
    }

    private static AnalysisPlan ToDomainPlan(JsonElement plan)
    {
        var input = plan.Deserialize(McpJsonContext.Default.McpPlanInput);
        return McpToolHelpers.ToDomainPlan(input);
    }
}
