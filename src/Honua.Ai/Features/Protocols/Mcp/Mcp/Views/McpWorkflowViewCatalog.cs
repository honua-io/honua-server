// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.Views;

/// <summary>
/// The server-authored workflow-view catalog (honua-server#3428). Today it holds
/// one view — <c>setup</c>, the bounded terminal path from a cold server to a
/// saved, reopenable map/dashboard with a submitted publication.
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
    /// <summary>The bounded terminal-setup view name.</summary>
    public const string SetupViewName = "setup";

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
            + "only the descriptors that path needs; the full paginated catalog stays available with no view "
            + "selected.",
        Revision = "setup.v1",
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
                    McpWorkflowViewMemberRule.Exact("honua_ops_health"),
                    McpWorkflowViewMemberRule.Exact("honua_admin_server_status"),
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
                    McpWorkflowViewMemberRule.Prefix("honua_op_import_"),
                    McpWorkflowViewMemberRule.Prefix("honua_op_connection_"),
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
                    McpWorkflowViewMemberRule.Prefix("honua_op_service_"),
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
                    McpWorkflowViewMemberRule.Exact("honua_cancel_job"),
                ],
            },
            new McpWorkflowViewStageDefinition
            {
                Id = "compose",
                Title = "Compose and save maps and dashboards",
                Description =
                    "Create, edit, validate, preview, save and reopen a Studio map or dashboard draft: layers, "
                    + "styles, visibility, view, widgets, interactions and controls.",
                Rules = [McpWorkflowViewMemberRule.Prefix("honua_studio_")],

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

    /// <summary>Every view this server publishes, keyed by name (ordinal).</summary>
    public static IReadOnlyDictionary<string, McpWorkflowViewDefinition> All { get; } =
        new Dictionary<string, McpWorkflowViewDefinition>(StringComparer.Ordinal)
        {
            [Setup.Name] = Setup,
        };

    /// <summary>The published view names, in stable ordinal order.</summary>
    public static IReadOnlyList<string> Names { get; } =
        All.Keys.OrderBy(static n => n, StringComparer.Ordinal).ToArray();

    /// <summary>Finds a published view by name, or <c>null</c> when unknown.</summary>
    public static McpWorkflowViewDefinition? Find(string? name) =>
        name is null ? null : All.GetValueOrDefault(name);
}
