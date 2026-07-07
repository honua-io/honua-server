// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.ServiceDefaults;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Ai.AiBuilder.Planning;

/// <summary>
/// Live implementation of <see cref="IPlanAnalysisService"/> backed by the
/// <c>WorkflowGeneration</c> seam (<see cref="IWorkflowGenerationService"/> →
/// the configured <see cref="IWorkflowGenerationProvider"/>, e.g. AWS Bedrock).
/// Turns an arbitrary natural-language GIS intent into an executable analysis
/// plan rather than matching a fixture, then hands the canonical
/// <see cref="McpAnalysisPlanOutput"/> back to the MCP <c>honua_plan_analysis</c>
/// tool so <c>validate_plan</c>/<c>dry_run_plan</c>/<c>execute_plan</c> operate on
/// it unchanged downstream.
/// </summary>
/// <remarks>
/// This service reuses the WorkflowGeneration orchestration wholesale: the
/// node-registry grounding, the config-driven provider selection
/// (<c>WorkflowGeneration:DefaultProvider</c>), and the hard validation gate all
/// run inside <see cref="IWorkflowGenerationService.GenerateAsync"/>. The only
/// new behaviour here is adapting the validated <see cref="WorkflowGraph"/> into
/// the geoprocessing analysis-plan shape via <see cref="WorkflowGraphPlanMapper"/>
/// and projecting the non-success turns (clarification / unsupported / refused)
/// onto the plan-analysis envelope. Auth and the operation policy/approval gates
/// are unchanged — they remain enforced by the MCP tool surface and the plan
/// executor.
/// </remarks>
internal sealed class LivePlanAnalysisService : IPlanAnalysisService
{
    private readonly IWorkflowGenerationService _generationService;
    private readonly IWorkflowNodeRegistry _nodeRegistry;
    private readonly ILogger<LivePlanAnalysisService> _logger;

    // PlanAnalysis:Provider override; null/blank means "use the WorkflowGeneration
    // default provider". Normalised once at construction.
    private readonly string? _providerOverride;

    public LivePlanAnalysisService(
        IWorkflowGenerationService generationService,
        IWorkflowNodeRegistry nodeRegistry,
        IOptions<PlanAnalysisConfiguration> options,
        ILogger<LivePlanAnalysisService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _generationService = generationService;
        _nodeRegistry = nodeRegistry;
        _logger = logger;

        _providerOverride = options.Value.NormalizedProvider();
    }

    /// <inheritdoc />
    public string Engine => "live";

    /// <inheritdoc />
    public async Task<McpPlanAnalysisOutput> PlanAsync(
        string intent,
        JsonElement? context,
        CancellationToken cancellationToken)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("honua.planner.live");

        WorkflowGenerationResult result;
        try
        {
            result = await _generationService
                .GenerateAsync(
                    new WorkflowGenerationRequest
                    {
                        Prompt = intent,

                        // Route the plan lane to the PlanAnalysis-selected provider
                        // when one is configured; null lets the WorkflowGeneration
                        // seam pick its configured default provider.
                        ProviderId = _providerOverride
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller-driven cancellation is not a planner failure; let it surface.
            throw;
        }
        catch (Exception exception)
        {
            // A provider transport/parse failure (e.g. the Anthropic Messages API
            // returning an error, a timeout, or malformed JSON) must not crash the
            // MCP tool. Surface it as a structured rejection so the SDK/console can
            // present it like any other non-success planner turn.
            LivePlanAnalysisLog.ProviderFailed(_logger, exception);
            activity?.SetTag("planner.status", "error");
            return new McpPlanAnalysisOutput
            {
                Status = "rejected",
                Reason = "The live planner provider failed to compile the intent into a plan.",
                Context = context
            };
        }

        activity?.SetTag("planner.provider", result.Provider);
        activity?.SetTag("planner.status", result.Status.ToString());

        var output = await ProjectAsync(intent, result, cancellationToken).ConfigureAwait(false);
        output.Context = context;
        return output;
    }

    private async Task<McpPlanAnalysisOutput> ProjectAsync(
        string intent,
        WorkflowGenerationResult result,
        CancellationToken cancellationToken)
    {
        switch (result.Status)
        {
            case WorkflowGenerationStatus.Generated when result.Graph is not null:
                {
                    var nodeDefinitions = await BuildNodeIndexAsync(result.Graph, cancellationToken)
                        .ConfigureAwait(false);
                    var intentId = "intent-" + Math.Abs(intent.GetHashCode(StringComparison.Ordinal))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var planId = "plan-" + (result.RegistryVersion ?? "live");

                    var plan = WorkflowGraphPlanMapper.ToAnalysisPlan(
                        result.Graph, planId, intentId, nodeDefinitions);

                    LivePlanAnalysisLog.Planned(_logger, result.Provider ?? "unknown", plan.Steps.Count);

                    return new McpPlanAnalysisOutput
                    {
                        Status = "planned",
                        Plan = plan,
                        ContractVersion = result.RegistryVersion is null
                            ? null
                            : "honua.workflow_generation." + result.RegistryVersion
                    };
                }

            case WorkflowGenerationStatus.NeedsClarification:
                return new McpPlanAnalysisOutput
                {
                    Status = "clarification_required",
                    Reason = result.Rationale ?? "The intent is ambiguous; clarification is required.",
                    Clarification = MapClarification(result.Clarifications)
                };

            case WorkflowGenerationStatus.Unsupported:
                return new McpPlanAnalysisOutput
                {
                    Status = "unsupported",
                    Reason = result.Rationale ?? "The requested capability is not supported on this server.",
                    CapabilityState = MapCapabilityState(result.CapabilityState)
                };

            default:
                // Refused or Error: surface as a rejection with the provider rationale.
                return new McpPlanAnalysisOutput
                {
                    Status = "rejected",
                    Reason = result.Rationale ?? "The planner could not compile the intent into a plan."
                };
        }
    }

    private async Task<Dictionary<string, WorkflowNodeDefinition>> BuildNodeIndexAsync(
        WorkflowGraph graph,
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, WorkflowNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (index.ContainsKey(node.NodeTypeId))
            {
                continue;
            }

            var definition = await _nodeRegistry
                .GetNodeAsync(node.NodeTypeId, cancellationToken)
                .ConfigureAwait(false);
            if (definition is not null)
            {
                index[node.NodeTypeId] = definition;
            }
        }

        return index;
    }

    private static McpPlanAnalysisClarification MapClarification(
        IReadOnlyList<WorkflowGenerationClarification> clarifications)
    {
        var candidates = new List<McpPlanAnalysisClarificationCandidate>(clarifications.Count);
        foreach (var clarification in clarifications)
        {
            candidates.Add(new McpPlanAnalysisClarificationCandidate
            {
                Kind = clarification.Kind,
                Field = clarification.Id,
                Reason = clarification.Reason ?? clarification.Prompt,
                Options = clarification.Choices.Select(choice => choice.Id).ToArray()
            });
        }

        return new McpPlanAnalysisClarification { Candidates = candidates };
    }

    private static McpPlanAnalysisCapabilityState? MapCapabilityState(
        WorkflowGenerationCapabilityState? capabilityState)
    {
        if (capabilityState is null)
        {
            return null;
        }

        return new McpPlanAnalysisCapabilityState
        {
            Name = capabilityState.Name,
            State = capabilityState.State,
            Reason = capabilityState.Reason
        };
    }
}
