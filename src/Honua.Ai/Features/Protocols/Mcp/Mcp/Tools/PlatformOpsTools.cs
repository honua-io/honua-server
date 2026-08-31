// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that returns the declared platform-release co-versioning snapshot.
/// </summary>
internal sealed class PlatformReleaseStatusTool(ILogger<PlatformReleaseStatusTool> logger) : IMcpTool
{
    public const string ToolName = "honua_platform_release_status";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Platform release status",
        Description =
            "Read-only deterministic platform_release / server_upgrade status. Mirrors the admin "
            + "platformRelease preflight projection and is distinct from hosted-content deployments.",
        InputSchema = McpPlatformOpsSchemas.PlatformReleaseStatusInputSchema,
        OutputSchema = McpPlatformOpsSchemas.PlatformReleaseStatusOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Platform release status")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetPlatformReleaseStatus");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);
        McpToolHelpers.EnsureNoArguments(arguments);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var payload = await ResolveReader(httpContext)
            .GetPlatformReleaseStatusAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessJsonElement(
            payload,
            static _ => "Platform-release status returned in structuredContent.");
    }

    private static IMcpPlatformOpsReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpPlatformOpsReader>();
}

/// <summary>
/// MCP tool that lists deploy operations or fetches one deploy operation by id.
/// </summary>
internal sealed class DeployOperationsTool(ILogger<DeployOperationsTool> logger) : IMcpTool
{
    public const string ToolName = "honua_deploy_operations";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Deploy operations",
        Description =
            "Read-only deterministic platform_release / server_upgrade deploy-operation read. "
            + "Omit operationId to list newest-first operations; provide operationId to fetch one.",
        InputSchema = McpPlatformOpsSchemas.DeployOperationsInputSchema,
        OutputSchema = McpPlatformOpsSchemas.DeployOperationsOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Deploy operations")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetDeployOperations");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseOptionalArguments(
            arguments,
            McpJsonContext.Default.McpDeployOperationsArgument);
        var payload = await ResolveReader(httpContext)
            .GetDeployOperationsAsync(principal, argument, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessJsonElement(
            payload,
            static _ => "Deploy operations returned in structuredContent.");
    }

    private static IMcpPlatformOpsReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpPlatformOpsReader>();
}

/// <summary>
/// MCP tool that reports operation kinds backed by the live executor catalog.
/// </summary>
internal sealed class SupportedOperationKindsTool(ILogger<SupportedOperationKindsTool> logger) : IMcpTool
{
    public const string ToolName = "honua_supported_operation_kinds";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Supported operation kinds",
        Description =
            "Read-only discovery of stable operation-kind identifiers backed by executors registered "
            + "in the live operation gateway. Use this before honua_propose_operation; absent kinds "
            + "remain fail-closed and cannot be routed.",
        InputSchema = McpPlatformOpsSchemas.SupportedOperationKindsInputSchema,
        OutputSchema = McpPlatformOpsSchemas.SupportedOperationKindsOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Supported operation kinds")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetSupportedOperationKinds");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);
        McpToolHelpers.EnsureNoArguments(arguments);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var output = await httpContext.RequestServices
            .GetRequiredService<IMcpPlatformOpsReader>()
            .GetSupportedOperationKindsAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(
            output,
            McpJsonContext.Default.McpSupportedOperationKindsOutput);
    }
}

/// <summary>
/// MCP tool that proposes a rollback as a new forward Deploy operation to a prior revision.
/// </summary>
internal sealed class ProposeRollbackTool(ILogger<ProposeRollbackTool> logger) : IMcpTool
{
    public const string ToolName = "honua_propose_rollback";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Propose rollback",
        Description =
            "Propose a platform rollback by routing OperationClass.Deploy through the operation gateway "
            + "with a new forward deploy to a prior revision. This is not an in-flight deploy abort/undo, "
            + "and no approve, submit, promote, or reject action is exposed to agents.",
        InputSchema = McpPlatformOpsSchemas.ProposeRollbackInputSchema,
        OutputSchema = McpToolOutputSchemas.ProposeOperationOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Propose rollback", destructive: false, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ProposeRollback");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseArguments(
            arguments,
            McpJsonContext.Default.McpProposeRollbackArgument);
        var output = await ResolveReader(httpContext)
            .ProposeRollbackAsync(principal, argument, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpProposeOperationOutput);
    }

    private static IMcpPlatformOpsReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpPlatformOpsReader>();
}
