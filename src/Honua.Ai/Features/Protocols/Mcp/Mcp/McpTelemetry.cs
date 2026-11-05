// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.Infrastructure.Security;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;

namespace Honua.Ai.Protocols.Mcp;

internal static class McpTelemetry
{
    public static readonly Counter<long> ToolCallCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.mcp.tool.call",
        unit: "{call}",
        description: "Count of MCP tool invocations by name, status and workflow family.");

    public static readonly Counter<long> BoundaryRejectionCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.mcp.boundary.rejection",
        unit: "{rejection}",
        description: "Count of MCP requests rejected for crossing taxonomy non-goals.");

    public static readonly Counter<long> ResourceReadCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.mcp.resource.read",
        unit: "{read}",
        description: "Count of MCP resource reads by family and status.");

    public static readonly Counter<long> GroundingResultCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.grounding.result",
        unit: "{result}",
        description: "Count of grounding passes by engine, classified workflow family, and whether a clarification envelope was emitted.");

    public static void RecordGroundingResult(string engine, string workflowFamily, bool clarified)
    {
        GroundingResultCount.Add(
            1,
            new KeyValuePair<string, object?>("engine", engine),
            new KeyValuePair<string, object?>("workflow_family", workflowFamily),
            new KeyValuePair<string, object?>("clarified", clarified ? "true" : "false"));
    }

    public static class Status
    {
        public const string Ok = "ok";
        public const string Error = "error";
    }

    public static class WorkflowFamily
    {
        public const string Planning = "planning";
        public const string Execution = "execution";
        public const string Lifecycle = "lifecycle";
        public const string Results = "results";
        public const string Unknown = "unknown";
    }

    public static class ResourceFamily
    {
        public const string TemporaryFiles = "temporary-files";
        public const string Jobs = "jobs";
        public const string JobResults = "job-results";
        public const string JobReports = "job-reports";
        public const string Proposals = "proposals";
        public const string Workspaces = "workspaces";
        public const string Catalog = "catalog";
        public const string PublishedServices = "published-services";
        public const string Deployments = "deployments";
        public const string MapPackages = "map-packages";
        public const string AppPackages = "app-packages";
        public const string PromotionIndex = "promotion-index";
        public const string OpsHealth = "ops-health";
        public const string OpsFindings = "ops-findings";
        public const string Unknown = "unknown";
    }

    public const string UnknownToolName = "unknown";

    public static class RejectionReason
    {
        public const string DataEditing = "data_editing";
        public const string DirectProtocol = "direct_protocol";
        public const string ServerInternals = "server_internals";
        public const string OutOfScope = "out_of_scope";
    }

    public static void EnrichActivity(string operation, HttpContext? httpContext = null)
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return;
        }

        activity.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Mcp);
        activity.SetTag(HonuaTelemetry.Tags.Operation, operation);

        var tenant = httpContext is null
            ? null
            : CanonicalSecurityActor.FindStampedValue(
                httpContext.User,
                CanonicalSecurityActor.EffectiveTenantClaim);
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            activity.SetTag("honua.tenant.id", tenant);
        }
    }
}
