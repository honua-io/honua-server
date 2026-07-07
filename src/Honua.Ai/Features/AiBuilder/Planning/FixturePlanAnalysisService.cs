// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.AiBuilder.Fixtures;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Ai.AiBuilder.Planning;

/// <summary>
/// Fixture-replay implementation of <see cref="IPlanAnalysisService"/>. Matches
/// the incoming intent string against the AI-builder contract fixtures and
/// translates the matched scenario into the canonical
/// <see cref="McpPlanAnalysisOutput"/> shape. Lets the SDK exercise the full
/// prompt → plan → apply contract deterministically without invoking a model.
/// </summary>
/// <remarks>
/// The service does not call grounding, the live planner, or any LLM. Live
/// planning is intentionally out of scope here — that ships behind a separate
/// implementation that the host can register as the resolved
/// <see cref="IPlanAnalysisService"/> when the planner service is available.
/// </remarks>
internal sealed class FixturePlanAnalysisService : IPlanAnalysisService
{
    private readonly AiBuilderFixtureCatalog _catalog;
    private readonly ILogger<FixturePlanAnalysisService> _logger;

    public FixturePlanAnalysisService(
        AiBuilderFixtureCatalog catalog,
        ILogger<FixturePlanAnalysisService> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Engine => "fixture";

    /// <inheritdoc />
    public Task<McpPlanAnalysisOutput> PlanAsync(
        string intent,
        JsonElement? context,
        CancellationToken cancellationToken)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.planner.fixture");

        var scenarioId = ReadContextString(context, "fixtureScenarioId")
            ?? ReadContextString(context, "scenarioId");
        var scenarioCase = ReadContextString(context, "fixtureCase");

        var scenario = _catalog.FindByPrompt(intent, scenarioId, scenarioCase);
        if (scenario is null)
        {
            activity?.SetTag("planner.match", "miss");
            return Task.FromResult(new McpPlanAnalysisOutput
            {
                Status = "rejected",

                // #2485: explicitly disclose fixture (demo) mode on the miss path.
                // The engine:"fixture" discriminator already flags every fixture
                // response, but the miss reason must also say — in plain language —
                // that this server replays canned demo plans and how to turn the
                // live planner on, so a caller (or a cold client LLM) does not read
                // "rejected" as "your intent is unsupported".
                Reason = "The plan analyzer is running in fixture (demo) mode: it replays canned "
                    + "plans for a fixed set of demo intents and found no fixture matching the "
                    + "supplied intent — it did not attempt to compile the intent. To compile "
                    + "arbitrary intents into executable plans, configure a live model provider "
                    + "(PlanAnalysis:Enabled=true with PlanAnalysis:Provider, or "
                    + "WorkflowGeneration:Enabled=true with a live DefaultProvider). See "
                    + "docs/guides/connect/mcp-live-planner.md.",
                Context = context
            });
        }

        activity?.SetTag("planner.match", "hit");
        activity?.SetTag("planner.fixture_case", scenario.Case);
        activity?.SetTag("planner.contract_version", scenario.ContractVersion);

        var output = AiBuilderScenarioMapper.ToPlanAnalysisOutput(scenario);
        output.Context = context;
        return Task.FromResult(output);
    }

    private static string? ReadContextString(JsonElement? context, string propertyName)
    {
        if (!context.HasValue || context.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return context.Value.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
