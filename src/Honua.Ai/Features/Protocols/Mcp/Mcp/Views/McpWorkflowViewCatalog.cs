// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.Views;

/// <summary>
/// The server-authored workflow-view catalog (honua-server#3428). <c>setup</c> remains the
/// older lifecycle view. <c>configure</c>, <c>operate</c>, and <c>analyze</c> are the 2026.1
/// views the curated prompts select.
/// </summary>
/// <remarks>
/// <para>
/// The catalog owns <em>selection</em> only. Every published descriptor —
/// description, annotations, input schema, output schema — is taken verbatim from
/// the canonical live catalog (<c>McpDataAccessSurface.GetAllToolsAsync</c>, the
/// same roster <c>tools/list</c> serves and the ADR-0058 capability registry
/// governs), so neither this server nor any client maintains a second
/// name/schema inventory.
/// </para>
/// <para>
/// Stages are matched first-match-wins in declaration order, and family prefix
/// rules make the view self-updating: an eligible server operation added to (or
/// removed from) a covered family joins or leaves the generated view with no edit
/// here and no SDK/Studio source-list edit.
/// </para>
/// </remarks>
internal static class McpWorkflowViewCatalog
{
    /// <summary>The bounded default discovery/workflow surface.</summary>
    public const string DefaultViewName = "default";

    /// <summary>The bounded terminal-setup view name.</summary>
    public const string SetupViewName = "setup";

    /// <summary>The 2026.1 configure view: closed admin roster, publish, style, and Studio.</summary>
    public const string ConfigureViewName = "configure";

    /// <summary>The 2026.1 operate view: health, findings, and one finding proposal.</summary>
    public const string OperateViewName = "operate";

    /// <summary>The 2026.1 analyze view: ground, plan, execute, and job control.</summary>
    public const string AnalyzeViewName = "analyze";

    /// <summary>
    /// The default meta/workflow surface. Long-tail operations are reached by
    /// resolving/searching, describing, planning/proposing, then executing.
    /// </summary>
    public static McpWorkflowViewDefinition Default { get; } = new()
    {
        Name = DefaultViewName,
        Title = "Bounded discovery workflow",
        Description = "The default bounded search, describe, propose, execute, and inspect surface.",
        Revision = "default.v1",
        Stages =
        [
            new McpWorkflowViewStageDefinition
            {
                Id = "discover",
                Title = "Search and describe",
                Description = "Find capabilities and entities, then inspect canonical layer metadata.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_list_capabilities"),
                    McpWorkflowViewMemberRule.Exact("honua_resolve_entity"),
                    McpWorkflowViewMemberRule.Exact("honua_supported_operation_kinds"),
                    McpWorkflowViewMemberRule.Exact("honua_list_layers"),
                    McpWorkflowViewMemberRule.Exact("honua_describe_layer"),
                    McpWorkflowViewMemberRule.Exact("honua_query_features"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "propose-execute",
                Title = "Propose and execute",
                Description = "Compile, validate, dry-run, execute, and inspect a bounded plan.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_plan_analysis"),
                    McpWorkflowViewMemberRule.Exact("honua_validate_plan"),
                    McpWorkflowViewMemberRule.Exact("honua_dry_run_plan"),
                    McpWorkflowViewMemberRule.Exact("honua_execute_plan"),
                    McpWorkflowViewMemberRule.Exact("honua_list_jobs"),
                    McpWorkflowViewMemberRule.Exact("honua_render_map"),
                ],
            },
        ],
    };

    /// <summary>
    /// The <c>setup</c> view: readiness → connect/import → publish → verify access
    /// → style/render → bounded geoprocessing → Studio composition/lifecycle →
    /// publication submit/status.
    /// </summary>
    public static McpWorkflowViewDefinition Setup { get; } = new()
    {
        Name = SetupViewName,
        Title = "Terminal setup workflow",
        Description =
            "The bounded server-authored path from a cold server to a saved, reopenable map or dashboard with a "
            + "submitted publication: confirm readiness, connect and import a source, publish it as a service and "
            + "layer, verify access, apply canonical style and render, run bounded geoprocessing, compose and save "
            + "Studio maps/dashboards, then submit a publication and poll its status. Select this view to receive "
            + "only the descriptors that path needs; the full paginated catalog is an explicit escape hatch.",
        Revision = "setup.v2",
        Stages =
        [
            new McpWorkflowViewStageDefinition
            {
                Id = "readiness",
                Title = "Readiness",
                Description =
                    "Confirm what this server can do right now and resolve human names to canonical identifiers "
                    + "before acting.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_list_capabilities"),
                    McpWorkflowViewMemberRule.Exact("honua_resolve_entity"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "connect-import",
                Title = "Connect and import",
                Description = "Bring a source dataset or connection onto the server.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_ingest_dataset"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "publish",
                Title = "Publish service and layer",
                Description = "Publish imported data as an addressable service and layer.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_publish_service"),
                    McpWorkflowViewMemberRule.Exact("honua_publish_result"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "verify-access",
                Title = "Verify access",
                Description =
                    "Prove the published service is reachable and returns the expected layers and features.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_list_layers"),
                    McpWorkflowViewMemberRule.Exact("honua_describe_layer"),
                    McpWorkflowViewMemberRule.Exact("honua_query_features"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "style-render",
                Title = "Canonical style and render",
                Description = "Read and apply canonical styling, then render a map image to confirm the result.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_get_style"),
                    McpWorkflowViewMemberRule.Exact("honua_apply_style_preset"),
                    McpWorkflowViewMemberRule.Exact("honua_render_map"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "geoprocessing",
                Title = "Bounded geoprocessing",
                Description =
                    "Plan, validate, dry-run and execute a bounded analysis, then track the resulting job.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_plan_analysis"),
                    McpWorkflowViewMemberRule.Exact("honua_validate_plan"),
                    McpWorkflowViewMemberRule.Exact("honua_dry_run_plan"),
                    McpWorkflowViewMemberRule.Exact("honua_execute_plan"),
                    McpWorkflowViewMemberRule.Exact("honua_list_jobs"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "compose",
                Title = "Compose and save maps and dashboards",
                Description =
                    "Create, edit, validate, preview, save and reopen a Studio map or dashboard draft: layers, "
                    + "styles, visibility, view, widgets, interactions and controls.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_studio_create_draft"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_validate_draft"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_get_draft"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_update_draft"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_preview_draft"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_save_version"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_reopen_version"),
                ],

                // Publication submit belongs to the publication stage below even
                // though it shares the studio_ family prefix.
                Exclusions = [McpWorkflowViewMemberRule.Exact("honua_studio_propose_publication")],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "publication",
                Title = "Submit publication and poll status",
                Description =
                    "Record publication intent on the saved draft, submit the governed operation proposal, and "
                    + "poll what the server will accept and what happened.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_studio_propose_publication"),
                    McpWorkflowViewMemberRule.Exact("honua_supported_operation_kinds"),
                ],
            },
        ],
    };

    /// <summary>
    /// The <c>configure</c> view. Prompts select this, not <c>setup</c>. Studio composition stays
    /// here unless the descriptor budget forces a <c>compose</c> split.
    /// </summary>
    public static McpWorkflowViewDefinition Configure { get; } = new()
    {
        Name = ConfigureViewName,
        Title = "Configure and publish",
        Description =
            "The bounded path an admin uses to connect or ingest data, publish a layer, set access, "
            + "style and render a map, then compose, save, read, and propose a Studio map, app, or dashboard.",
        Revision = "configure.v1",
        Stages =
        [
            new McpWorkflowViewStageDefinition
            {
                Id = "admin",
                Title = "Closed admin roster",
                Description = "Read server status and API-key grants, then create, test, import, publish, and set access.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_admin_server_status"),
                    McpWorkflowViewMemberRule.Exact("honua_admin_api_key_list"),
                    McpWorkflowViewMemberRule.Exact("honua_admin_api_key_effective_permissions"),
                    McpWorkflowViewMemberRule.Exact("honua_admin_connections_create"),
                    McpWorkflowViewMemberRule.Exact("honua_admin_connections_test"),
                    McpWorkflowViewMemberRule.Exact("honua_admin_import_upload_url"),
                    McpWorkflowViewMemberRule.Exact("honua_admin_layer_publish"),
                    McpWorkflowViewMemberRule.Exact("honua_admin_services_access_policy_set"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "connect-import",
                Title = "Connect and import",
                Description = "Inline CSV or GeoJSON ingest, up to 4 MB.",
                Rules = [McpWorkflowViewMemberRule.Exact("honua_ingest_dataset")],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "publish",
                Title = "Publish",
                Description = "Publish a service or an analysis result.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_publish_service"),
                    McpWorkflowViewMemberRule.Exact("honua_publish_result"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "verify-access",
                Title = "Verify access",
                Description = "List, describe, and query the published layers.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_list_layers"),
                    McpWorkflowViewMemberRule.Exact("honua_describe_layer"),
                    McpWorkflowViewMemberRule.Exact("honua_query_features"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "style-render",
                Title = "Style and render",
                Description = "Read a style, apply a preset, and render a PNG.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_get_style"),
                    McpWorkflowViewMemberRule.Exact("honua_apply_style_preset"),
                    McpWorkflowViewMemberRule.Exact("honua_render_map"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "studio",
                Title = "Studio lifecycle",
                Description = "Create, edit, validate, save, read, reopen, and propose publication.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_studio_create_draft"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_update_draft"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_get_draft"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_validate_draft"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_preview_draft"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_save_version"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_get_version"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_reopen_version"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_propose_publication"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "composition",
                Title = "Studio composition",
                Description = "Layers, style, visibility, view, widgets, controls, and interactions.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_studio_add_layer"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_remove_layer"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_set_layer_style"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_set_layer_visibility"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_set_view"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_add_widget"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_remove_widget"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_bind_interaction"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_remove_interaction"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_add_control"),
                    McpWorkflowViewMemberRule.Exact("honua_studio_remove_control"),
                ],
            },
        ],
    };

    /// <summary>The <c>operate</c> view: read operational evidence and propose one finding.</summary>
    public static McpWorkflowViewDefinition Operate { get; } = new()
    {
        Name = OperateViewName,
        Title = "Operate",
        Description = "Read health, findings, events, release status, and deploy operations, then propose one finding.",
        Revision = "operate.v1",
        Stages =
        [
            new McpWorkflowViewStageDefinition
            {
                Id = "observe",
                Title = "Observe",
                Description = "Read the operational evidence an admin can see without a second operator.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_ops_health"),
                    McpWorkflowViewMemberRule.Exact("honua_ops_findings"),
                    McpWorkflowViewMemberRule.Exact("honua_operate_events"),
                    McpWorkflowViewMemberRule.Exact("honua_alert_events"),
                    McpWorkflowViewMemberRule.Exact("honua_platform_release_status"),
                    McpWorkflowViewMemberRule.Exact("honua_deploy_operations"),
                    McpWorkflowViewMemberRule.Exact("honua_supported_operation_kinds"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "remediate",
                Title = "Propose a finding",
                Description = "An admin applies one finding they just proposed. A non-admin proposal waits.",
                Rules = [McpWorkflowViewMemberRule.Exact("honua_propose_finding")],
            },
        ],
    };

    /// <summary>
    /// The <c>analyze</c> view named by the site, hazard, and permit prompts before publish.
    /// </summary>
    public static McpWorkflowViewDefinition Analyze { get; } = new()
    {
        Name = AnalyzeViewName,
        Title = "Analyze",
        Description =
            "Ground a question, hand-author a plan when honua_plan_analysis returns engine fixture, "
            + "then validate, execute, and read the job.",
        Revision = "analyze.v1",
        Stages =
        [
            new McpWorkflowViewStageDefinition
            {
                Id = "ground",
                Title = "Ground",
                Description = "Resolve layers, features, an address, and the caller's intent.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_ground_candidates"),
                    McpWorkflowViewMemberRule.Exact("honua_clarify_intent"),
                    McpWorkflowViewMemberRule.Exact("honua_list_layers"),
                    McpWorkflowViewMemberRule.Exact("honua_query_features"),
                    McpWorkflowViewMemberRule.Exact("honua_geocode_address"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "plan",
                Title = "Plan and execute",
                Description = "Validate and run a plan. A published-layer buffer is analytics.buffer-aggregate.",
                Rules =
                [
                    McpWorkflowViewMemberRule.Exact("honua_plan_analysis"),
                    McpWorkflowViewMemberRule.Exact("honua_validate_plan"),
                    McpWorkflowViewMemberRule.Exact("honua_dry_run_plan"),
                    McpWorkflowViewMemberRule.Exact("honua_execute_plan"),
                    McpWorkflowViewMemberRule.Exact("honua_list_jobs"),
                    McpWorkflowViewMemberRule.Exact("honua_cancel_job"),
                ],
            },
        ],
    };

    /// <summary>Every view this server publishes, keyed by name (ordinal).</summary>
    public static IReadOnlyDictionary<string, McpWorkflowViewDefinition> All { get; } =
        new Dictionary<string, McpWorkflowViewDefinition>(StringComparer.Ordinal)
        {
            [Default.Name] = Default,
            [Setup.Name] = Setup,
            [Configure.Name] = Configure,
            [Operate.Name] = Operate,
            [Analyze.Name] = Analyze,
        };

    /// <summary>The published view names, in stable ordinal order.</summary>
    public static IReadOnlyList<string> Names { get; } =
        All.Keys.OrderBy(static n => n, StringComparer.Ordinal).ToArray();

    /// <summary>Finds a published view by name, or <c>null</c> when unknown.</summary>
    public static McpWorkflowViewDefinition? Find(string? name) =>
        name is null ? null : All.GetValueOrDefault(name);
}
