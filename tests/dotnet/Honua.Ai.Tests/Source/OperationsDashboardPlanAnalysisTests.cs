// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.AiBuilder.Fixtures;
using Honua.Server.Features.AiBuilder.Planning;
using Honua.Geoprocessing;
using Honua.Server.Features.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Pins the honua_plan_analysis output for the operations-dashboard app-builder
/// family. The dashboard scenario exercises the spec-draft + app-package
/// surface of the planner — separate from the spatial-query family which only
/// emits an analysis plan — so coverage lives in its own fixture.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class OperationsDashboardPlanAnalysisTests
{
    private const string DashboardPrompt =
        "Build an operations dashboard for this saved map showing a map, incident list, incident count, incidents by type chart, and district filter.";

    private static PlanAnalysisTool CreateTool() =>
        new(
            new FixturePlanAnalysisService(
                new AiBuilderFixtureCatalog(),
                NullLogger<FixturePlanAnalysisService>.Instance),
            Substitute.For<IGeoprocessingJobService>(),
            NullLogger<PlanAnalysisTool>.Instance);

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_OperationsDashboardSuccess_EmitsSpecDraftWithCanonicalNodes()
    {
        var tool = CreateTool();
        var arguments = McpTestFactory.ParseJson(
            $$"""{"intent":"{{DashboardPrompt}}"}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("planned");
        body.GetProperty("contractVersion").GetString().Should().Be("honua.ai_builder.operations_dashboard.v1");

        var specDraft = body.GetProperty("specDraft");
        specDraft.GetProperty("reviewStatus").GetString().Should().Be("reviewable");
        specDraft.GetProperty("specKind").GetString().Should().Be("CanonicalSpecDocument");
        specDraft.GetProperty("grammarVersion").GetString().Should().Be("v1.0");
        specDraft.GetProperty("processFamilyVersion").GetString()
            .Should().Be("ai-builder.operations-dashboard.v1");

        var nodeIds = specDraft.GetProperty("nodes")
            .EnumerateArray()
            .Select(n => n.GetProperty("id").GetString())
            .ToArray();
        nodeIds.Should().Contain([
            "incident-events-source",
            "response-districts-source",
            "incident-list",
            "incident-count",
            "incidents-by-type",
            "district-filter",
            "operations-dashboard-app"
        ]);

        var kinds = specDraft.GetProperty("nodes")
            .EnumerateArray()
            .Select(n => n.GetProperty("kind").GetString())
            .ToHashSet();
        kinds.Should().Contain(["Service", "Report", "Compute", "App"]);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_OperationsDashboardSuccess_EmitsAppPackagePreviewWithWidgetsAndBindings()
    {
        var tool = CreateTool();
        var arguments = McpTestFactory.ParseJson(
            $$"""{"intent":"{{DashboardPrompt}}"}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        var appPackage = body.GetProperty("appPackage");
        appPackage.GetProperty("appPackageId").GetString().Should().Be("app-pkg-ops-dashboard");
        appPackage.GetProperty("templateId").GetString().Should().Be("operations-dashboard");
        appPackage.GetProperty("targetSdk").GetString().Should().Be("honua-sdk-js");
        appPackage.GetProperty("mapPackageId").GetString().Should().Be("map-pkg-ops-dashboard");
        appPackage.GetProperty("entryPoint").GetString().Should().Be("src/main.tsx");
        appPackage.GetProperty("manifestArtifactId").GetString().Should().Be("artifact-app-manifest-ops");
        appPackage.GetProperty("bundleArtifactId").GetString().Should().Be("artifact-app-bundle-ops");

        var widgets = appPackage.GetProperty("widgets")
            .EnumerateArray()
            .Select(w => w.GetString())
            .ToArray();
        widgets.Should().BeEquivalentTo(["map", "incident-list", "incident-count", "incidents-by-type", "district-filter"]);

        var bindings = appPackage.GetProperty("dataBindings");
        bindings.GetProperty("incidentSource").GetString().Should().Be("catalog:layer:incident_events");
        bindings.GetProperty("districtSource").GetString().Should().Be("catalog:layer:response_districts");
        bindings.GetProperty("chartGroupBy").GetString().Should().Be("incident_type");

        var generatedFiles = appPackage.GetProperty("generatedFiles")
            .EnumerateArray()
            .Select(f => f.GetString())
            .ToArray();
        generatedFiles.Should().Contain("src/honua-app-manifest.json");

        appPackage.GetProperty("deliveryHints").GetProperty("hostingMode").GetString().Should().Be("static_site");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_OperationsDashboardSuccess_SurfacesMutableSourceAndPromotionWarnings()
    {
        var tool = CreateTool();
        var arguments = McpTestFactory.ParseJson(
            $$"""{"intent":"{{DashboardPrompt}}"}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        var warningCodes = body.GetProperty("warnings")
            .EnumerateArray()
            .Select(w => w.GetProperty("code").GetString())
            .ToHashSet();
        warningCodes.Should().Contain([
            "mutable_source_cache_warning",
            "mcp_promotion_surface_gated"
        ]);

        body.GetProperty("cache").GetProperty("key").GetString().Should().Be("sha256:fixture-operations-dashboard");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_OperationsDashboardAmbiguity_EnumeratesAllSixCandidateKinds()
    {
        var tool = CreateTool();
        var arguments = McpTestFactory.ParseJson(
            """{"intent":"Build an incident dashboard from this map and group things by area."}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("clarification_required");
        var candidateKinds = body.GetProperty("clarification")
            .GetProperty("candidates")
            .EnumerateArray()
            .Select(c => c.GetProperty("kind").GetString())
            .ToHashSet();
        candidateKinds.Should().Contain(["source", "field", "geometry", "crs", "predicate", "aggregation"]);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_OperationsDashboardUnsupported_FlagsKernelDensityCapability()
    {
        var tool = CreateTool();
        var arguments = McpTestFactory.ParseJson(
            """{"intent":"Build the incident dashboard with a kernel density hot spot layer and compare it by district."}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("unsupported");
        body.GetProperty("capabilityState").GetProperty("name").GetString()
            .Should().Be("kernelDensityAggregation");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysis_OperationsDashboardCacheHit_CanSelectDuplicatePromptScenario()
    {
        var tool = CreateTool();
        var arguments = McpTestFactory.ParseJson(
            $$"""
            {
              "intent": "{{DashboardPrompt}}",
              "context": {
                "fixtureScenarioId": "cache-hit-reused-operations-dashboard"
              }
            }
            """);

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("fixtureCase").GetString().Should().Be("cache-hit");
        body.GetProperty("cache").GetProperty("hit").GetBoolean().Should().BeTrue();
        body.GetProperty("warnings").EnumerateArray()
            .Should().Contain(w => w.GetProperty("code").GetString() == "cache_hit");
    }
}
