// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.NlQuery.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.AiBuilder.Fixtures;
using Honua.Server.Features.AiBuilder.Planning;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.NlQuery;
using Honua.Server.Features.Protocols.Mcp.Tools;
using Honua.Server.Tests.Features.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.AiBuilder;

/// <summary>
/// End-to-end exercise of the AI app-builder platform contract: the same prompt
/// flows through the deterministic NL filter planner, the honua_plan_analysis
/// MCP tool, and (for the operations-dashboard family) the app-package preview.
/// One fixture catalog backs all three services, so this test proves that the
/// canned outputs stay consistent across the chain rather than diverging at
/// each layer.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class AiBuilderEndToEndContractTests
{
    private const string SpatialQueryPrompt =
        "Show open hospitals within 1 km of flood zones as a linked map, table, and chart.";

    private const string OperationsDashboardPrompt =
        "Build an operations dashboard for this saved map showing a map, incident list, incident count, incidents by type chart, and district filter.";

    private static readonly LayerDefinition FacilitiesLayer = new(
        Id: 1,
        Name: "critical_facilities",
        Description: "Fixture critical facilities layer",
        GeometryType: GeometryType.Point,
        SpatialReference: SpatialReference.WGS84,
        Fields:
        [
            new FieldDefinition("facility_type", FieldType.String, Length: 50),
            new FieldDefinition("status", FieldType.String, Length: 20),
            new FieldDefinition("capacity", FieldType.Integer),
            new FieldDefinition("shape", FieldType.Geometry)
        ]);

    private static (DeterministicNlQueryPlanProvider Nl, PlanAnalysisTool Planner) BuildServices()
    {
        var catalog = new AiBuilderFixtureCatalog();
        var nl = new DeterministicNlQueryPlanProvider(catalog, NullLogger<DeterministicNlQueryPlanProvider>.Instance);
        var planner = new PlanAnalysisTool(
            new FixturePlanAnalysisService(catalog, NullLogger<FixturePlanAnalysisService>.Instance),
            Substitute.For<IGeoprocessingJobService>(),
            NullLogger<PlanAnalysisTool>.Instance);
        return (nl, planner);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task SpatialQueryPrompt_FlowsFromNlPlanToAnalysisPlanWithConsistentWarnings()
    {
        var (nl, planner) = BuildServices();

        // Stage 1: NL planner produces a structured FilterPlan from the prompt.
        var nlResult = await nl.GeneratePlanAsync(
            new NlQueryPlanRequest(SpatialQueryPrompt, FacilitiesLayer, "critical_facilities"));

        nlResult.IsSuccess.Should().BeTrue();
        nlResult.Plan.Should().NotBeNull();
        nlResult.Plan!.Combinator.Should().Be(FilterPlanCombinator.And);
        nlResult.Plan.Clauses.Should().Contain(c =>
            c.Type == "spatial" && c.Spatial != null && c.Spatial.Operator == "dwithin");

        var orchestrator = new NlQueryOrchestrator(nl, NullLogger<NlQueryOrchestrator>.Instance);
        var compiled = await orchestrator.ExecuteAsync(
            new NlQueryPlanRequest(SpatialQueryPrompt, FacilitiesLayer, "critical_facilities"));
        compiled.IsSuccess.Should().BeTrue(compiled.ErrorMessage);

        // Stage 2: same prompt drives plan analysis through MCP. The two
        // services share the AI-builder catalog so the success scenario must
        // resolve identically.
        var arguments = McpTestFactory.ParseJson(
            $$"""{"intent":"{{SpatialQueryPrompt}}"}""");
        var planResult = await planner.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = planResult.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("planned");
        body.GetProperty("contractVersion").GetString().Should().Be("honua.ai_builder.spatial_query.v1");

        var stepKinds = body.GetProperty("plan").GetProperty("steps")
            .EnumerateArray()
            .Select(s => s.GetProperty("kind").GetString())
            .ToArray();
        stepKinds.Should().Contain(["QueryFeatures", "Geoprocess", "Export"]);

        // Stage 3: warnings carried across both surfaces are stable — the
        // NL stage doesn't carry warnings of its own, but the plan stage
        // must keep surfacing mutable_source_cache_warning so that fixture-
        // backed clients see the same reproducibility hint the live server
        // would emit on a mutable source.
        body.GetProperty("warnings").EnumerateArray()
            .Should().Contain(w => w.GetProperty("code").GetString() == "mutable_source_cache_warning");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task OperationsDashboardPrompt_EmitsSpecDraftPlanAndAppPackagePreview()
    {
        var (_, planner) = BuildServices();

        var arguments = McpTestFactory.ParseJson(
            $$"""{"intent":"{{OperationsDashboardPrompt}}"}""");

        var result = await planner.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("planned");
        body.GetProperty("contractVersion").GetString().Should().Be("honua.ai_builder.operations_dashboard.v1");

        // SpecDraft (canonical) → matches process family
        body.GetProperty("specDraft").GetProperty("processFamilyVersion").GetString()
            .Should().Be("ai-builder.operations-dashboard.v1");

        // AnalysisPlan (lowered DAG) → all expected steps present
        var stepIds = body.GetProperty("plan").GetProperty("steps")
            .EnumerateArray()
            .Select(s => s.GetProperty("stepId").GetString())
            .ToArray();
        stepIds.Should().Contain([
            "incident-events-source",
            "compose-map-package",
            "compose-app-package",
            "emit-app-manifest"
        ]);

        // AppPackage preview → widgets and data bindings ready for the SDK
        var appPackage = body.GetProperty("appPackage");
        appPackage.GetProperty("appPackageId").GetString().Should().Be("app-pkg-ops-dashboard");
        appPackage.GetProperty("widgets").EnumerateArray()
            .Select(w => w.GetString())
            .Should().Contain("district-filter");
        appPackage.GetProperty("dataBindings").GetProperty("chartGroupBy").GetString()
            .Should().Be("incident_type");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task ClarificationAmbiguityPrompt_SurfacesStructuredCandidatesNotOpaqueFailure()
    {
        var (nl, planner) = BuildServices();

        // The NL filter planner returns failure (the fixture doesn't carry a
        // filterPlan for clarification scenarios — that's by design), but the
        // failure carries a fixture-case reason so callers know to fall back
        // to grounding rather than treat the prompt as a hard error.
        var nlResult = await nl.GeneratePlanAsync(
            new NlQueryPlanRequest("Find shelters near flood zones.", FacilitiesLayer));
        nlResult.IsSuccess.Should().BeFalse();
        nlResult.ErrorMessage.Should().Contain("ambiguity");

        // The plan analysis path is the authoritative surface for
        // clarification — it returns the structured candidate envelope the
        // SDK uses to render disambiguation UI.
        var arguments = McpTestFactory.ParseJson(
            """{"intent":"Find shelters near flood zones."}""");
        var planResult = await planner.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        var body = planResult.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("clarification_required");
        var candidates = body.GetProperty("clarification").GetProperty("candidates")
            .EnumerateArray()
            .ToArray();
        candidates.Should().HaveCountGreaterThan(0);
        candidates.Should().OnlyContain(c =>
            c.GetProperty("kind").ValueKind == System.Text.Json.JsonValueKind.String);
        candidates.Should().OnlyContain(c =>
            c.GetProperty("options").ValueKind == System.Text.Json.JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task CacheHitPrompt_ReturnsSameCacheKeyAsOriginatingSuccessScenario()
    {
        var (_, planner) = BuildServices();

        var firstArgs = McpTestFactory.ParseJson(
            $$"""{"intent":"{{SpatialQueryPrompt}}"}""");
        var firstResult = await planner.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), firstArgs, CancellationToken.None);
        var firstCacheKey = firstResult.StructuredContent!.Value
            .GetProperty("cache").GetProperty("key").GetString();

        // The cache-hit scenario shares the same prompt as the success
        // scenario. Fixture replay uses context to select that duplicate case
        // while preserving the cache key that SDKs use for artifact reuse.
        var secondArgs = McpTestFactory.ParseJson(
            $$"""
            {
              "intent": "{{SpatialQueryPrompt}}",
              "context": {
                "fixtureCase": "cache-hit"
              }
            }
            """);
        var secondResult = await planner.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), secondArgs, CancellationToken.None);
        var secondCache = secondResult.StructuredContent!.Value.GetProperty("cache");
        var secondCacheKey = secondCache.GetProperty("key").GetString();

        secondCacheKey.Should().Be(firstCacheKey);
        secondCache.GetProperty("hit").GetBoolean().Should().BeTrue();
        firstCacheKey.Should().StartWith("sha256:");
    }
}
