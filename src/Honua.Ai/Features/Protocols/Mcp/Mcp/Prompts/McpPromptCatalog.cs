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
                + "Optimize for these criteria: {criteria}.\n"
                + "Select tools/list view \"analyze\". You are an admin and finish this analysis in this session.\n\n"
                + "Drive the workflow with the honua MCP tools on that view:\n"
                + "1. Call honua_ground_candidates to resolve the candidate datasets and study area.\n"
                + "2. Call honua_plan_analysis to compile a suitability plan; honua_clarify_intent if anything is ambiguous. "
                + "If its engine field is \"fixture\", the plan is a demo (not compiled from your intent): follow the response's nextSteps to hand-author a plan from the honua://catalog/processes catalog instead.\n"
                + "3. When the plan buffers a published layer, use process analytics.buffer-aggregate and pass that layer's layerId. "
                + "geometry.buffer accepts one WKB geometry and must not be used to buffer a published layer.\n"
                + "4. Call honua_validate_plan and honua_dry_run_plan to confirm the plan is executable and to estimate cost.\n"
                + "5. Call honua_execute_plan; poll the honua://jobs/{jobId} resource until the job completes, and track it with honua_list_jobs.\n"
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
                "You are assessing exposure to {hazard} for the assets in {exposureLayer} across {studyArea}.\n"
                + "Select tools/list view \"analyze\". You are an admin and finish this assessment in this session.\n\n"
                + "Drive the workflow with the honua MCP tools on that view:\n"
                + "1. Call honua_list_layers to confirm the hazard and exposure layers exist; honua_query_features to inspect them.\n"
                + "2. Call honua_ground_candidates and honua_plan_analysis to compile an overlay/intersection plan. "
                + "If honua_plan_analysis returns engine=\"fixture\", the plan is a demo (not compiled from your intent): follow its nextSteps to hand-author a plan from honua://catalog/processes instead.\n"
                + "3. When the plan buffers a published layer, use process analytics.buffer-aggregate and pass that layer's layerId. "
                + "geometry.buffer accepts one WKB geometry and must not be used to buffer a published layer.\n"
                + "4. Call honua_validate_plan, then honua_execute_plan, and poll honua://jobs/{jobId}.\n"
                + "5. Read honua://jobs/{jobId}/report for the rendered analysis report.\n"
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
                + "Check it against these constraints: {constraints}.\n"
                + "Select tools/list view \"analyze\" for the review. You are an admin and finish this review in this session.\n\n"
                + "Drive the workflow with the honua MCP tools on that view:\n"
                + "1. Call honua_query_features to retrieve the parcel geometry and attributes.\n"
                + "2. Call honua_geocode_address if only an address is known to resolve the parcel.\n"
                + "3. Call honua_plan_analysis to compile the constraint-overlay plan (if it returns engine=\"fixture\", the plan is a demo — follow its nextSteps to hand-author a plan from honua://catalog/processes), then honua_validate_plan and honua_execute_plan.\n"
                + "4. When the plan buffers a published layer, use process analytics.buffer-aggregate and pass that layer's layerId. "
                + "geometry.buffer accepts one WKB geometry and must not be used to buffer a published layer.\n"
                + "5. To publish the decision layer, switch tools/list to view \"configure\" and call only honua_publish_service. "
                + "It returns the published service. Do not hand that call to anyone else.\n"
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
                + "Start from these layers if provided: {layers}.\n"
                + "Select tools/list view \"configure\". You are an admin and finish the dashboard in this session.\n\n"
                + "Drive the workflow with the honua MCP tools on that view:\n"
                + "1. Call honua_list_layers to discover candidate layers and honua_query_features to validate their contents.\n"
                + "2. Call honua_render_map to preview the basemap and operational layers together.\n"
                + "3. Call honua_studio_create_draft with family dashboard. Set the view with honua_studio_set_view, "
                + "add widgets with honua_studio_add_widget, and add controls with honua_studio_add_control.\n"
                + "4. Call honua_studio_validate_draft, then honua_studio_save_version. Read the saved version with "
                + "honua_studio_get_version (versionId and contentHash are top-level on that result; save nests them under version).\n"
                + "5. Call honua_studio_propose_publication. An admin's result includes the share URL. Finish publication in this session.\n"
                + "Hand back the dashboard layout, the included layers, and the share URL."),

        new McpPromptDefinition(
            Name: "setup_and_publish",
            Title: "Set up and publish",
            Description:
                "Take a cold server to a published service and a saved, reopenable map or dashboard "
                + "with a submitted publication, entirely through MCP tool calls.",
            Arguments:
            [
                new McpPromptArgumentDefinition(
                    "source",
                    "The dataset to import: inline CSV or GeoJSON content (up to 4 MB), or a description of it.",
                    Required: true),
                new McpPromptArgumentDefinition(
                    "serviceName",
                    "The service name to publish the imported data under.",
                    Required: false),
                new McpPromptArgumentDefinition(
                    "deliverable",
                    "What to compose from the published layers (e.g. \"map\", \"dashboard\").",
                    Required: false),
            ],
            Template:
                "You are setting up this Honua server and publishing {source} as the service {serviceName}, "
                + "delivered as a {deliverable}.\n"
                + "Select tools/list view \"configure\". You are an admin and finish configuration, publication, "
                + "and the Studio share URL in this session.\n"
                + "Work only through MCP tool calls; the Console and browser Studio are not required.\n\n"
                + "Drive the workflow with the honua MCP tools on that view:\n"
                + "1. Either call honua_ingest_dataset with inline CSV or GeoJSON content (up to 4 MB), its format "
                + "and a datasetName, then honua_publish_service (honua_publish_result for an analysis output); "
                + "or create a PostGIS connection by secret reference with honua_admin_connections_create "
                + "(secretReference and secretType, never a password), confirm it with honua_admin_connections_test "
                + "(the connection argument is id), import a small file with honua_admin_import_upload_url, "
                + "publish the table with honua_admin_layer_publish using flat fields connectionId, schema, table, "
                + "and layerName, and set anonymous read with honua_admin_services_access_policy_set "
                + "(serviceName, allowAnonymous, allowAnonymousWrite). Secret create and rotate are not MCP tools.\n"
                + "2. Verify access with honua_list_layers, honua_describe_layer and honua_query_features.\n"
                + "3. Call honua_get_style and honua_apply_style_preset, then honua_render_map to confirm the result.\n"
                + "4. Call honua_studio_create_draft, edit it with honua_studio_update_draft, and compose it with "
                + "honua_studio_add_layer, honua_studio_set_layer_style, honua_studio_set_layer_visibility, "
                + "honua_studio_set_view, honua_studio_add_widget, honua_studio_add_control, and "
                + "honua_studio_bind_interaction. Read it back with honua_studio_get_draft, and check it with "
                + "honua_studio_validate_draft and honua_studio_preview_draft.\n"
                + "5. Call honua_studio_save_version (versionId and contentHash are nested under version) and "
                + "honua_studio_get_version (those two fields are top-level). Reopen with honua_studio_reopen_version "
                + "when you need another draft.\n"
                + "6. Call honua_studio_propose_publication. An admin's result includes the share URL.\n"
                + "Report the published service and layer identifiers, the saved version, and the share URL."),
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
