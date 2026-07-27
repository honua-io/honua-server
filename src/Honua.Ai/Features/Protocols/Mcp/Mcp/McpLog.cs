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

    [LoggerMessage(8163, LogLevel.Warning, "MCP tool invocation failed unexpectedly: ToolName={ToolName}")]
    public static partial void ToolFailedUnexpected(ILogger logger, string toolName, Exception exception);

    [LoggerMessage(8164, LogLevel.Warning, "MCP resource read failed unexpectedly: ResourceFamily={ResourceFamily}, Uri={Uri}")]
    public static partial void ResourceReadFailed(ILogger logger, string resourceFamily, string uri, Exception exception);

    [LoggerMessage(8165, LogLevel.Warning, "MCP dispatch failed unexpectedly: Method={Method}")]
    public static partial void DispatchFailed(ILogger logger, string method, Exception exception);

    [LoggerMessage(8166, LogLevel.Debug, "MCP session issued: SessionId={SessionId}")]
    public static partial void SessionIssued(ILogger logger, string sessionId);

    [LoggerMessage(8167, LogLevel.Information, "MCP session rejected: Reason={Reason}")]
    public static partial void SessionRejected(ILogger logger, string reason);

    [LoggerMessage(8168, LogLevel.Debug, "MCP session terminated: SessionId={SessionId}, Found={Found}")]
    public static partial void SessionTerminated(ILogger logger, string sessionId, bool found);

    [LoggerMessage(8169, LogLevel.Debug, "MCP notification published: Method={Method}, SessionId={SessionId}")]
    public static partial void NotificationPublished(ILogger logger, string method, string sessionId);

    [LoggerMessage(8170, LogLevel.Debug, "MCP notification broadcast: Method={Method}, Sessions={SessionCount}")]
    public static partial void NotificationBroadcast(ILogger logger, string method, int sessionCount);

    [LoggerMessage(8171, LogLevel.Debug, "MCP job progress bridge stopped: SessionId={SessionId}, JobId={JobId}, Reason={Reason}")]
    public static partial void ProgressBridgeStopped(ILogger logger, string sessionId, string jobId, string reason);

    [LoggerMessage(8172, LogLevel.Warning, "MCP progress bridge stopped for session {SessionId} job {JobId}: job read failed.")]
    public static partial void ProgressBridgeJobReadFailed(ILogger logger, string sessionId, string jobId, Exception exception);

    /// <summary>
    /// Per-call audit record for a Studio draft lifecycle/composition tool
    /// (honua-server#3002, NFR-001): session identity (the resolved principal
    /// key) plus the draft generation before and after the call. Emitted by
    /// every Studio tool, including read-only calls (generation before/after
    /// equal) and failed calls (generation after omitted/unchanged).
    /// </summary>
    [LoggerMessage(
        8173,
        LogLevel.Information,
        "MCP Studio draft audited: Tool={ToolName}, Principal={PrincipalKey}, DraftId={DraftId}, GenerationBefore={GenerationBefore}, GenerationAfter={GenerationAfter}")]
    public static partial void StudioDraftAudited(
        ILogger logger,
        string toolName,
        string principalKey,
        string draftId,
        long? generationBefore,
        long? generationAfter);

    /// <summary>
    /// A POST presented a well-formed but unknown <c>Mcp-Session-Id</c> and was
    /// served statelessly instead of 404 (honua-server#3027 fallback for
    /// multi-instance deployments without sticky routing). Only a short prefix
    /// of the id is logged because a session id names a live session elsewhere.
    /// </summary>
    [LoggerMessage(8174, LogLevel.Debug, "MCP unknown session served statelessly: SessionIdPrefix={SessionIdPrefix}")]
    public static partial void SessionServedStateless(ILogger logger, string sessionIdPrefix);
}
