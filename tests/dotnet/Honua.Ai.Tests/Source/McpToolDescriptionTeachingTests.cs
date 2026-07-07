// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.AiBuilder.Fixtures;
using Honua.Ai.AiBuilder.Planning;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Discovery;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Pins the load-bearing teaching phrases in the MCP planning-tool descriptions
/// and output schemas (the tool-description quality pass, #1948/#2485). Tool
/// descriptions are prompts: an agent reading only <c>tools/list</c> plus the
/// plan_analysis output must learn (a) how to route among the natural-language
/// entry tools, (b) the full execute->poll->results->publish loop, (c) whether a
/// plan came from a live model or a fixture, and (d) which processIds exist and
/// where their parameter docs live. These assertions fail if a future edit drops
/// the teaching, catching prompt-quality regressions the compiler cannot.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpToolDescriptionTeachingTests
{
    private static readonly IGeoprocessingJobService JobService = Substitute.For<IGeoprocessingJobService>();
    private static readonly IGroundingService GroundingService = Substitute.For<IGroundingService>();

    private static string ExecutePlanDescription =>
        new ExecutePlanTool(JobService, NullLogger<ExecutePlanTool>.Instance).Describe().Description;

    private static string ResolveEntityDescription =>
        new ResolveEntityTool(JobService, NullLogger<ResolveEntityTool>.Instance).Describe().Description;

    private static string GroundCandidatesDescription =>
        new GroundCandidatesTool(GroundingService, JobService, NullLogger<GroundCandidatesTool>.Instance)
            .Describe().Description;

    private static string PlanAnalysisDescription =>
        new PlanAnalysisTool(
                Substitute.For<IPlanAnalysisService>(), JobService, NullLogger<PlanAnalysisTool>.Instance)
            .Describe().Description;

    private static string ClarifyIntentDescription =>
        new ClarifyIntentTool(GroundingService, JobService, NullLogger<ClarifyIntentTool>.Instance)
            .Describe().Description;

    private static string ValidatePlanDescription =>
        new ValidatePlanTool(JobService, NullLogger<ValidatePlanTool>.Instance).Describe().Description;

    private static string DryRunPlanDescription =>
        new DryRunPlanTool(JobService, NullLogger<DryRunPlanTool>.Instance).Describe().Description;

    // -------------------------------------------------------------------
    // Item 1: the execute->poll->results->publish loop, taught where agents
    // look (the execute_plan tool description in tools/list).
    // -------------------------------------------------------------------

    [UnitTest]
    public void ExecutePlanDescription_TeachesThePollResultsPublishLoop()
    {
        var description = ExecutePlanDescription;

        description.Should().Contain("jobId");
        description.Should().Contain("honua://jobs/{jobId}");
        description.Should().Contain("resources/read");
        description.Should().Contain("terminal");
        description.Should().Contain("honua://jobs/{jobId}/results");
        description.Should().Contain("honua_publish_result");
    }

    // -------------------------------------------------------------------
    // Item 2: when-to-use routing for the three NL-entry tools + the clarify
    // continuation + the two pre-flight one-liners.
    // -------------------------------------------------------------------

    [UnitTest]
    public void ResolveEntityDescription_RoutesToLayerLookup()
    {
        var description = ResolveEntityDescription;

        description.Should().Contain("NAME");
        description.Should().Contain("layer references");
        description.Should().Contain("find a layer");
    }

    [UnitTest]
    public void GroundCandidatesDescription_RoutesToGoalExploration()
    {
        var description = GroundCandidatesDescription;

        description.Should().Contain("GOAL");
        description.Should().Contain("workflow family");
        description.Should().Contain("explore what is possible before planning");
    }

    [UnitTest]
    public void PlanAnalysisDescription_RoutesToIntentPlanning_AndDisclosesEngine()
    {
        var description = PlanAnalysisDescription;

        description.Should().Contain("INTENT");
        description.Should().Contain("already know what you want done");
        // Item 3: fixture-mode disclosure surfaced in the description too.
        description.Should().Contain("engine");
        description.Should().Contain("fixture");
        description.Should().Contain("capability demo");
    }

    [UnitTest]
    public void ClarifyIntentDescription_RoutesToGroundingContinuation_AndDocumentsIntentIdProvenance()
    {
        var description = ClarifyIntentDescription;

        description.Should().Contain("honua_ground_candidates");
        description.Should().Contain("intentId");
    }

    [UnitTest]
    public void ValidatePlanDescription_StatesReturnsAndPreFlightPairing()
    {
        var description = ValidatePlanDescription;

        description.Should().Contain("violations[]");
        description.Should().Contain("warnings[]");
        description.Should().Contain("pre-flight");
        description.Should().Contain("honua_dry_run_plan");
    }

    [UnitTest]
    public void DryRunPlanDescription_StatesEstimatesAndPreFlightPairing()
    {
        var description = DryRunPlanDescription;

        description.Should().Contain("estimatedDurationSeconds");
        description.Should().Contain("sideEffects");
        description.Should().Contain("without executing");
        description.Should().Contain("honua_validate_plan");
    }

    // -------------------------------------------------------------------
    // Item 3: the engine field is surfaced from whichever planner ran.
    // -------------------------------------------------------------------

    [UnitTest]
    public void FixturePlanAnalysisService_ReportsFixtureEngine()
    {
        var service = new FixturePlanAnalysisService(
            new AiBuilderFixtureCatalog(), NullLogger<FixturePlanAnalysisService>.Instance);

        service.Engine.Should().Be("fixture");
    }

    [UnitTest]
    public void LivePlanAnalysisService_ReportsLiveEngine()
    {
        var service = new LivePlanAnalysisService(
            Substitute.For<IWorkflowGenerationService>(),
            Substitute.For<IWorkflowNodeRegistry>(),
            Options.Create(new PlanAnalysisConfiguration()),
            NullLogger<LivePlanAnalysisService>.Instance);

        service.Engine.Should().Be("live");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_plan_analysis")]
    public async Task PlanAnalysisTool_SurfacesFixtureEngineOnTheOutput()
    {
        var tool = new PlanAnalysisTool(
            new FixturePlanAnalysisService(
                new AiBuilderFixtureCatalog(), NullLogger<FixturePlanAnalysisService>.Instance),
            JobService,
            NullLogger<PlanAnalysisTool>.Instance);
        var arguments = McpTestFactory.ParseJson(
            """{"intent":"Show open hospitals within 1 km of flood zones as a linked map, table, and chart."}""");

        var result = await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("engine").GetString().Should().Be("fixture");
    }

    [UnitTest]
    public void PlanAnalysisOutputSchema_DeclaresEngineFieldAndDisclosure()
    {
        var schema = McpToolOutputSchemas.PlanAnalysisOutputSchema;

        schema.GetProperty("required").EnumerateArray().Select(x => x.GetString())
            .Should().Contain("engine");

        var engine = schema.GetProperty("properties").GetProperty("engine");
        engine.GetProperty("enum").EnumerateArray().Select(x => x.GetString())
            .Should().BeEquivalentTo("live", "fixture");
        engine.GetProperty("description").GetString().Should().Contain("capability demo");
    }

    // -------------------------------------------------------------------
    // Item 4: Geoprocess plan steps name the valid processIds (sourced from the
    // process catalog registry) and point agents at honua://catalog/processes.
    // -------------------------------------------------------------------

    [UnitTest]
    public void ProcessIdNames_AreSourcedFromTheBuiltInProcessCatalog()
    {
        var catalogIds = new BuiltInProcessCatalog()
            .ListProcesses()
            .Select(process => process.ProcessId)
            .ToArray();

        McpToolSchemas.ProcessIdNames.Should().BeEquivalentTo(catalogIds);
        McpToolSchemas.ProcessIdNames.Should().Contain("geometry.buffer");
    }

    [UnitTest]
    public void ExecutePlanSchema_ProcessIdAndInputs_DocumentCatalogDiscovery()
    {
        AssertPlanStepDocumentsCatalog(McpToolSchemas.ExecutePlanArgumentSchema);
    }

    [UnitTest]
    public void PlanArgumentSchema_ProcessIdAndInputs_DocumentCatalogDiscovery()
    {
        AssertPlanStepDocumentsCatalog(McpToolSchemas.PlanArgumentSchema);
    }

    private static void AssertPlanStepDocumentsCatalog(JsonElement planSchema)
    {
        var stepProperties = planSchema
            .GetProperty("properties").GetProperty("plan")
            .GetProperty("properties").GetProperty("steps")
            .GetProperty("items").GetProperty("properties");

        var processId = stepProperties.GetProperty("processId");
        processId.GetProperty("description").GetString().Should().Contain("honua://catalog/processes");

        var examples = processId.GetProperty("examples").EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();
        examples.Should().Contain("geometry.buffer");
        examples.Should().BeEquivalentTo(McpToolSchemas.ProcessIdNames);

        var inputs = stepProperties.GetProperty("inputs");
        var inputsDescription = inputs.GetProperty("description").GetString();
        inputsDescription.Should().Contain("string-encoded");
        inputsDescription.Should().Contain("honua://catalog/processes");
    }
}
