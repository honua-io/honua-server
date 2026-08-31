// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Prompts;

/// <summary>
/// The curated catalog of reusable geospatial workflow prompts advertised on the
/// MCP <c>prompts/list</c> / <c>prompts/get</c> surface (#1953). Each prompt is a
/// templated, multi-step entrypoint that steers a client LLM through one of the
/// canonical Honua workflows — site selection, hazard assessment, permit review,
/// and dashboard scaffolding — by naming the honua_* tools and honua:// resources
/// it should drive. The roster mirrors the prompt families in
/// <c>AI_OPERATOR_CONTRACT.md §Prompts</c>.
/// </summary>
/// <remarks>
/// The catalog is static and process-wide: prompt templates carry no runtime
/// state, so the surface advertises <c>prompts.listChanged: false</c> and renders
/// purely by substituting client-supplied arguments. Argument substitution uses
/// <c>{argumentName}</c> placeholders; required arguments are validated before
/// rendering so a missing input surfaces a structured
/// <see cref="GeoprocessingValidationException"/> rather than a half-rendered
/// prompt.
/// </remarks>
internal static class McpPromptCatalog
{
    private static readonly IReadOnlyList<McpPromptDefinition> Definitions =
    [
        new McpPromptDefinition(
            Name: "site_selection_analysis",
            Title: "Site selection analysis",
            Description:
                "Plan and run a multi-criteria site selection: ground the candidate "
                + "datasets, validate the plan, and execute it as a durable job.",
            Arguments:
            [
                new McpPromptArgumentDefinition(
                    "objective",
                    "What the site is for (e.g. \"a new fire station\", \"an EV charging hub\").",
                    Required: true),
                new McpPromptArgumentDefinition(
                    "studyArea",
                    "The area of interest as a place name, bbox, or known service/layer.",
                    Required: true),
                new McpPromptArgumentDefinition(
                    "criteria",
                    "Suitability criteria to weigh (e.g. \"near arterial roads, away from flood zones\").",
                    Required: false),
            ],
            Template:
                "You are running a site selection analysis for {objective} within {studyArea}.\n"
                + "Optimize for these criteria: {criteria}.\n\n"
                + "Drive the workflow with the honua MCP tools:\n"
                + "1. Call honua_ground_candidates to resolve the candidate datasets and study area.\n"
                + "2. Call honua_plan_analysis to compile a suitability plan; honua_clarify_intent if anything is ambiguous. "
                + "If its engine field is \"fixture\", the plan is a demo (not compiled from your intent): follow the response's nextSteps to hand-author a plan from the honua://catalog/processes catalog instead.\n"
                + "3. Call honua_validate_plan and honua_dry_run_plan to confirm the plan is executable and to estimate cost.\n"
                + "4. Call honua_execute_plan; poll the honua://jobs/{jobId} resource until the job completes.\n"
                + "Summarize the ranked candidate sites and cite the honua://jobs/{jobId}/results artifacts."),

        new McpPromptDefinition(
            Name: "hazard_assessment",
            Title: "Hazard assessment",
            Description:
                "Assess exposure of assets or population to a hazard (flood, wildfire, "
                + "seismic) and produce an evidence-backed analysis report.",
            Arguments:
            [
                new McpPromptArgumentDefinition(
                    "hazard",
                    "The hazard to assess (e.g. \"100-year flood\", \"wildfire\", \"liquefaction\").",
                    Required: true),
                new McpPromptArgumentDefinition(
                    "exposureLayer",
                    "The assets or population layer to evaluate exposure for.",
                    Required: true),
                new McpPromptArgumentDefinition(
                    "studyArea",
                    "The area of interest as a place name, bbox, or known service/layer.",
                    Required: false),
            ],
            Template:
                "You are assessing exposure to {hazard} for the assets in {exposureLayer} across {studyArea}.\n\n"
                + "Drive the workflow with the honua MCP tools:\n"
                + "1. Call honua_list_layers to confirm the hazard and exposure layers exist; honua_query_features to inspect them.\n"
                + "2. Call honua_ground_candidates and honua_plan_analysis to compile an overlay/intersection plan. "
                + "If honua_plan_analysis returns engine=\"fixture\", the plan is a demo (not compiled from your intent): follow its nextSteps to hand-author a plan from honua://catalog/processes instead.\n"
                + "3. Call honua_validate_plan, then honua_execute_plan, and poll honua://jobs/{jobId}.\n"
                + "4. Read honua://jobs/{jobId}/report for the rendered analysis report.\n"
                + "Report the count and share of exposed assets and the most affected sub-areas."),

        new McpPromptDefinition(
            Name: "permit_review",
            Title: "Permit review",
            Description:
                "Review a permit application against zoning, setback, and overlay "
                + "constraints and prepare an approval-ready decision package.",
            Arguments:
            [
                new McpPromptArgumentDefinition(
                    "parcel",
                    "The subject parcel identifier or address.",
                    Required: true),
                new McpPromptArgumentDefinition(
                    "permitType",
                    "The permit type under review (e.g. \"ADU\", \"commercial change of use\").",
                    Required: true),
                new McpPromptArgumentDefinition(
                    "constraints",
                    "Constraint layers to check (e.g. \"zoning, setbacks, flood overlay, historic district\").",
                    Required: false),
            ],
            Template:
                "You are reviewing a {permitType} permit for parcel {parcel}.\n"
                + "Check it against these constraints: {constraints}.\n\n"
                + "Drive the workflow with the honua MCP tools:\n"
                + "1. Call honua_query_features to retrieve the parcel geometry and attributes.\n"
                + "2. Call honua_geocode_address if only an address is known to resolve the parcel.\n"
                + "3. Call honua_plan_analysis to compile the constraint-overlay plan (if it returns engine=\"fixture\", the plan is a demo — follow its nextSteps to hand-author a plan from honua://catalog/processes), then honua_validate_plan and honua_execute_plan.\n"
                + "4. If publishing a decision layer, call honua_propose_operation and respect any approval-required outcome before honua_publish_service.\n"
                + "Summarize each constraint as pass/fail with the supporting honua://jobs/{jobId}/results evidence."),

        new McpPromptDefinition(
            Name: "dashboard_scaffolding",
            Title: "Dashboard scaffolding",
            Description:
                "Scaffold an operations dashboard or field map from existing layers: "
                + "select the layers, render a preview map, and stage a publishable package.",
            Arguments:
            [
                new McpPromptArgumentDefinition(
                    "topic",
                    "What the dashboard monitors (e.g. \"active incidents\", \"utility outages\").",
                    Required: true),
                new McpPromptArgumentDefinition(
                    "audience",
                    "Who uses it (e.g. \"field crews\", \"emergency operations center\").",
                    Required: false),
                new McpPromptArgumentDefinition(
                    "layers",
                    "Layers to include, if already known.",
                    Required: false),
            ],
            Template:
                "You are scaffolding a {topic} dashboard for {audience}.\n"
                + "Start from these layers if provided: {layers}.\n\n"
                + "Drive the workflow with the honua MCP tools:\n"
                + "1. Call honua_list_layers to discover candidate layers and honua_query_features to validate their contents.\n"
                + "2. Call honua_render_map to preview the basemap and operational layers together.\n"
                + "3. Call honua_plan_analysis to compile the app/dashboard scaffold (if it returns engine=\"fixture\", the plan is a demo — follow its nextSteps to hand-author a plan from honua://catalog/processes), then honua_validate_plan.\n"
                + "4. Call honua_propose_operation to stage the publishable package and respect any approval-required outcome.\n"
                + "Hand back the dashboard layout, the included layers, and the staged package resource URI."),
    ];

    private static readonly Dictionary<string, McpPromptDefinition> ByName =
        Definitions.ToDictionary(p => p.Name, StringComparer.Ordinal);

    /// <summary>
    /// Describes every catalog prompt for <c>prompts/list</c>, ordered by name so
    /// the advertised surface is deterministic.
    /// </summary>
    public static McpPromptsListResult List() => new()
    {
        Prompts = Definitions
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Describe())
            .ToList(),
    };

    /// <summary>
    /// Renders the named prompt for <c>prompts/get</c>, substituting the
    /// client-supplied <paramref name="arguments"/> into the template.
    /// </summary>
    /// <exception cref="GeoprocessingValidationException">
    /// The prompt name is unknown, or a required argument was omitted or blank.
    /// </exception>
    public static McpPromptGetResult Get(string name, IReadOnlyDictionary<string, string>? arguments)
    {
        if (!ByName.TryGetValue(name, out var definition))
        {
            throw new GeoprocessingValidationException($"Unknown MCP prompt '{name}'.");
        }

        return definition.Render(arguments);
    }
}
