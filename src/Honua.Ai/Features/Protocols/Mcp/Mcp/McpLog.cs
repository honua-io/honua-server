// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Source-generated structured log methods for the MCP operator surface.
/// Event ID range 8150-8199, reserved after GPServerLog (8100-8149).
/// </summary>
internal static partial class McpLog
{
    [LoggerMessage(8150, LogLevel.Debug, "MCP tool invoked: ToolName={ToolName}, Family={WorkflowFamily}")]
    public static partial void ToolInvoked(ILogger logger, string toolName, string workflowFamily);

    [LoggerMessage(8151, LogLevel.Information, "MCP tool completed: ToolName={ToolName}, Status={Status}")]
    public static partial void ToolCompleted(ILogger logger, string toolName, string status);

    [LoggerMessage(8152, LogLevel.Warning, "MCP tool failed: ToolName={ToolName}, Code={ErrorCode}, Message={Message}")]
    public static partial void ToolFailed(ILogger logger, string toolName, string errorCode, string message);

    [LoggerMessage(8153, LogLevel.Debug, "MCP resource read: ResourceFamily={ResourceFamily}, Uri={Uri}")]
    public static partial void ResourceRead(ILogger logger, string resourceFamily, string uri);

    [LoggerMessage(8154, LogLevel.Information, "MCP authorization denied: ToolOrResource={Target}, Authenticated={Authenticated}")]
    public static partial void AuthorizationDenied(ILogger logger, string target, bool authenticated);

    [LoggerMessage(8155, LogLevel.Information, "MCP boundary rejection: Target={Target}, Reason={Reason}")]
    public static partial void BoundaryRejection(ILogger logger, string target, string reason);

    [LoggerMessage(8156, LogLevel.Debug, "MCP stub tool invoked: ToolName={ToolName}, BlockedBy={BlockedBy}")]
    public static partial void StubToolInvoked(ILogger logger, string toolName, string blockedBy);

    [LoggerMessage(8157, LogLevel.Information, "MCP operator surface initialized: Tools={ToolCount}, Resources={ResourceCount}")]
    public static partial void SurfaceInitialized(ILogger logger, int toolCount, int resourceCount);

    [LoggerMessage(8158, LogLevel.Warning, "MCP request parse failed: Error={Error}")]
    public static partial void RequestParseFailed(ILogger logger, string error);

    [LoggerMessage(8159, LogLevel.Debug, "MCP published-service read: ServiceId={ServiceId}, Status={Status}")]
    public static partial void PublishedServiceRead(ILogger logger, string serviceId, string status);

    [LoggerMessage(8160, LogLevel.Debug, "MCP deployment read: DeploymentId={DeploymentId}, Status={Status}")]
    public static partial void DeploymentRead(ILogger logger, string deploymentId, string status);

    [LoggerMessage(8161, LogLevel.Debug, "MCP package read: PackageKind={PackageKind}, PackageId={PackageId}, DeploymentCount={DeploymentCount}")]
    public static partial void PackageRead(ILogger logger, string packageKind, string packageId, int deploymentCount);

    [LoggerMessage(8162, LogLevel.Debug, "MCP promotion list read: ResourceFamily={ResourceFamily}, Count={Count}, Truncated={Truncated}")]
    public static partial void PromotionListRead(ILogger logger, string resourceFamily, int count, bool truncated);
}
