// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that returns the deterministic operational-health snapshot.
/// </summary>
internal sealed class OpsHealthTool(ILogger<OpsHealthTool> logger) : IMcpTool
{
    public const string ToolName = "honua_ops_health";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Ops health",
        Description =
            "Read-only deterministic ops-health snapshot. Mirrors the admin ops-health GET surface and "
            + "returns the honua://ops/health resource projection with no side effects.",
        InputSchema = McpOpsObservabilitySchemas.OpsHealthInputSchema,
        OutputSchema = McpOpsObservabilitySchemas.OpsHealthOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Ops health")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetOpsHealth");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);
        McpToolHelpers.EnsureNoArguments(arguments);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var payload = await ResolveReader(httpContext)
            .GetOpsHealthAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessJsonElement(
            payload,
            static _ => "Ops health snapshot returned in structuredContent.");
    }

    private static IMcpOpsObservabilityReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpOpsObservabilityReader>();
}

/// <summary>
/// MCP tool that lists or fetches deterministic operational findings.
/// </summary>
internal sealed class OpsFindingsTool(ILogger<OpsFindingsTool> logger) : IMcpTool
{
    public const string ToolName = "honua_ops_findings";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Ops findings",
        Description =
            "Read-only deterministic ops-findings query. Mirrors the admin findings GET surface; "
            + "omit findingId to list active findings or provide findingId to fetch one. "
            + "Recommended actions are descriptive only and must be proposed separately.",
        InputSchema = McpOpsObservabilitySchemas.OpsFindingsInputSchema,
        OutputSchema = McpOpsObservabilitySchemas.OpsFindingsOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Ops findings")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetOpsFindings");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseOptionalArguments(
            arguments,
            McpJsonContext.Default.McpOpsFindingsArgument);
        var payload = await ResolveReader(httpContext)
            .GetOpsFindingsAsync(principal, argument, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessJsonElement(
            payload,
            static _ => "Ops findings returned in structuredContent.");
    }

    private static IMcpOpsObservabilityReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpOpsObservabilityReader>();
}

/// <summary>
/// MCP tool that queries cursor-paged alert events.
/// </summary>
internal sealed class AlertEventsTool(ILogger<AlertEventsTool> logger) : IMcpTool
{
    public const string ToolName = "honua_alert_events";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Alert events",
        Description =
            "Read-only deterministic alert-event query over GIS alert events and ops notifications. "
            + "Supports bounded pageSize, cursor, time range, severity, lifecycle, source, and rule filters.",
        InputSchema = McpOpsObservabilitySchemas.AlertEventsInputSchema,
        OutputSchema = McpOpsObservabilitySchemas.AlertEventsOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Alert events")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ListAlertEvents");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseOptionalArguments(
            arguments,
            McpJsonContext.Default.McpAlertEventsArgument);
        var payload = await ResolveReader(httpContext)
            .ListAlertEventsAsync(principal, argument, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessJsonElement(
            payload,
            static _ => "Alert-event page returned in structuredContent.");
    }

    private static IMcpOpsObservabilityReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpOpsObservabilityReader>();
}

/// <summary>
/// MCP tool that queries the fused Operate event timeline.
/// </summary>
internal sealed class OperateEventsTool(ILogger<OperateEventsTool> logger) : IMcpTool
{
    public const string ToolName = "honua_operate_events";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Operate events",
        Description =
            "Read-only deterministic fused Operate timeline query. Mirrors the admin operate-events GET "
            + "surface and supports bounded pageSize plus kind, correlationId, operationId, releaseId, "
            + "and time-range filters.",
        InputSchema = McpOpsObservabilitySchemas.OperateEventsInputSchema,
        OutputSchema = McpOpsObservabilitySchemas.OperateEventsOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Operate events")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ListOperateEvents");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseOptionalArguments(
            arguments,
            McpJsonContext.Default.McpOperateEventsArgument);
        var payload = await ResolveReader(httpContext)
            .ListOperateEventsAsync(principal, argument, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessJsonElement(
            payload,
            static _ => "Operate-event page returned in structuredContent.");
    }

    private static IMcpOpsObservabilityReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpOpsObservabilityReader>();
}
