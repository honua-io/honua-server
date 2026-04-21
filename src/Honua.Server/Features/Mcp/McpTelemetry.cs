// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Mcp;

/// <summary>
/// Telemetry primitives for the MCP operator surface.
/// Defines shared activity tag names, counters, and workflow-family labels
/// so tools and resources enrich spans and metrics consistently.
/// </summary>
internal static class McpTelemetry
{
    /// <summary>
    /// MCP tool-call counter. Tags: <c>tool_name</c>, <c>status</c>,
    /// <c>workflow_family</c>.
    /// </summary>
    public static readonly Counter<long> ToolCallCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.mcp.tool.call",
        unit: "{call}",
        description: "Count of MCP tool invocations by name, status and workflow family.");

    /// <summary>
    /// MCP boundary-rejection counter for requests denied by taxonomy non-goals.
    /// Tags: <c>rejection_reason</c>.
    /// </summary>
    public static readonly Counter<long> BoundaryRejectionCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.mcp.boundary.rejection",
        unit: "{rejection}",
        description: "Count of MCP requests rejected for crossing taxonomy non-goals.");

    /// <summary>
    /// MCP resource-read counter. Tags: <c>resource_family</c>, <c>status</c>.
    /// </summary>
    public static readonly Counter<long> ResourceReadCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.mcp.resource.read",
        unit: "{read}",
        description: "Count of MCP resource reads by family and status.");

    /// <summary>
    /// Grounding-pass outcome counter. Tags: <c>engine</c>,
    /// <c>workflow_family</c>, <c>clarified</c>. Incremented once per
    /// successful grounding pass so honua-server-734 eval runs can watch engine
    /// mix and clarification rate over time.
    /// </summary>
    public static readonly Counter<long> GroundingResultCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.grounding.result",
        unit: "{result}",
        description: "Count of grounding passes by engine, classified workflow family, and whether a clarification envelope was emitted.");

    /// <summary>
    /// Records a grounding-pass outcome sample on <see cref="GroundingResultCount"/>.
    /// </summary>
    public static void RecordGroundingResult(string engine, string workflowFamily, bool clarified)
    {
        GroundingResultCount.Add(
            1,
            new KeyValuePair<string, object?>("engine", engine),
            new KeyValuePair<string, object?>("workflow_family", workflowFamily),
            new KeyValuePair<string, object?>("clarified", clarified ? "true" : "false"));
    }

    /// <summary>
    /// Well-known status-tag values.
    /// </summary>
    public static class Status
    {
        public const string Ok = "ok";
        public const string Error = "error";
        public const string NotImplemented = "not_implemented";
    }

    /// <summary>
    /// Workflow-family tag values aligned with the taxonomy capability matrix.
    /// <see cref="Unknown"/> is the sentinel emitted when the dispatcher rejects
    /// a <c>tools/call</c> before resolving the tool name (e.g. anonymous auth
    /// denial) so dashboards still see a counter sample.
    /// </summary>
    public static class WorkflowFamily
    {
        public const string Planning = "planning";
        public const string Execution = "execution";
        public const string Lifecycle = "lifecycle";
        public const string Results = "results";
        public const string Unknown = "unknown";
    }

    /// <summary>
    /// Resource-family tag values matching registered MCP URI families.
    /// <see cref="Unknown"/> is the sentinel emitted when the dispatcher rejects
    /// a <c>resources/read</c> before resolving the URI (e.g. anonymous auth
    /// denial) so dashboards still see a counter sample.
    /// </summary>
    public static class ResourceFamily
    {
        public const string Jobs = "jobs";
        public const string JobResults = "job-results";
        public const string Workspaces = "workspaces";
        public const string Catalog = "catalog";
        public const string PublishedServices = "published-services";
        public const string Deployments = "deployments";
        public const string MapPackages = "map-packages";
        public const string AppPackages = "app-packages";
        public const string PromotionIndex = "promotion-index";
        public const string Unknown = "unknown";
    }

    /// <summary>
    /// Sentinel <c>tool_name</c> tag value emitted when the dispatcher rejects
    /// a <c>tools/call</c> before the requested tool name is known.
    /// </summary>
    public const string UnknownToolName = "unknown";

    /// <summary>
    /// Rejection-reason tag values for <see cref="BoundaryRejectionCount"/>.
    /// Mirrors the non-goals list in the geospatial-mcp taxonomy.
    /// </summary>
    public static class RejectionReason
    {
        public const string DataEditing = "data_editing";
        public const string DirectProtocol = "direct_protocol";
        public const string ServerInternals = "server_internals";
        public const string OutOfScope = "out_of_scope";
    }

    /// <summary>
    /// Enriches the current activity with <c>honua.protocol = "Mcp"</c> and the
    /// supplied operation name. Mirrors the gRPC/GPServer adapter pattern.
    /// </summary>
    public static void EnrichActivity(string operation)
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return;
        }

        activity.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Mcp);
        activity.SetTag(HonuaTelemetry.Tags.Operation, operation);
    }
}
