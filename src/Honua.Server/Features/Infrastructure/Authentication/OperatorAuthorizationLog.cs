using Honua.Core.Features.Authorization.Domain;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Source-generated structured logging for operator authorization decisions.
/// </summary>
internal static partial class OperatorAuthorizationLog
{
    [LoggerMessage(
        EventId = 4700,
        Level = LogLevel.Debug,
        Message = "Operator authorization denied: authentication required for {ResourceType}.{Operation}")]
    public static partial void AuthenticationRequired(
        ILogger logger, OperatorResourceType resourceType, OperatorOperation operation);

    [LoggerMessage(
        EventId = 4701,
        Level = LogLevel.Debug,
        Message = "Operator authorization granted: admin bypass for principal {PrincipalId} on {ResourceType}.{Operation}")]
    public static partial void AdminBypassed(
        ILogger logger, string? principalId, OperatorResourceType resourceType, OperatorOperation operation);

    [LoggerMessage(
        EventId = 4702,
        Level = LogLevel.Information,
        Message = "Operator authorization denied: principal {PrincipalId} is not workspace owner {WorkspaceOwnerId}")]
    public static partial void WorkspaceOwnershipDenied(
        ILogger logger, string? principalId, string workspaceOwnerId);

    [LoggerMessage(
        EventId = 4703,
        Level = LogLevel.Debug,
        Message = "Operator authorization granted: principal {PrincipalId} for {ResourceType}.{Operation} on resource {ResourceId}")]
    public static partial void PermissionGranted(
        ILogger logger, string? principalId, OperatorResourceType resourceType, OperatorOperation operation, string? resourceId);

    [LoggerMessage(
        EventId = 4704,
        Level = LogLevel.Information,
        Message = "Operator authorization denied: principal {PrincipalId} lacks permission for {ResourceType}.{Operation} on resource {ResourceId}")]
    public static partial void PermissionDenied(
        ILogger logger, string? principalId, OperatorResourceType resourceType, OperatorOperation operation, string? resourceId);

    [LoggerMessage(
        EventId = 4705,
        Level = LogLevel.Warning,
        Message = "Unrecognized operator resource type '{ServiceValue}' in permission grant — skipping convention mapping")]
    public static partial void UnrecognizedResourceType(ILogger logger, string serviceValue);

    [LoggerMessage(
        EventId = 4706,
        Level = LogLevel.Information,
        Message = "Operator approval required: principal {PrincipalId} for {ResourceType}.{Operation} — policy {PolicyRef}")]
    public static partial void ApprovalRequired(
        ILogger logger, string? principalId, OperatorResourceType resourceType, OperatorOperation operation, string? policyRef);

    [LoggerMessage(
        EventId = 4707,
        Level = LogLevel.Debug,
        Message = "Operator approval not required: principal {PrincipalId} for {ResourceType}.{Operation}")]
    public static partial void ApprovalNotRequired(
        ILogger logger, string? principalId, OperatorResourceType resourceType, OperatorOperation operation);

    [LoggerMessage(
        EventId = 4708,
        Level = LogLevel.Warning,
        Message = "Operator authorization denied: personal workspace request from principal {PrincipalId} missing owner context")]
    public static partial void PersonalWorkspaceMissingOwner(ILogger logger, string? principalId);

    [LoggerMessage(
        EventId = 4709,
        Level = LogLevel.Debug,
        Message = "Operator authorization granted: public workspace {Operation} on resource {ResourceId}")]
    public static partial void PublicWorkspaceAllowed(
        ILogger logger, OperatorOperation operation, string? resourceId);

    [LoggerMessage(
        EventId = 4710,
        Level = LogLevel.Warning,
        Message = "Operator authorization denied: shared workspace request from principal {PrincipalId} missing scope context")]
    public static partial void SharedWorkspaceMissingScope(ILogger logger, string? principalId);

    [LoggerMessage(
        EventId = 4711,
        Level = LogLevel.Information,
        Message = "Operator authorization denied: principal {PrincipalId} is not in workspace scope {WorkspaceScopeId}")]
    public static partial void SharedWorkspaceScopeDenied(
        ILogger logger, string? principalId, string workspaceScopeId);

    [LoggerMessage(
        EventId = 4712,
        Level = LogLevel.Warning,
        Message = "Operator authorization denied: workspace request from principal {PrincipalId} missing visibility context")]
    public static partial void WorkspaceMissingVisibility(ILogger logger, string? principalId);

    [LoggerMessage(
        EventId = 4713,
        Level = LogLevel.Information,
        Message = "Operator authorization denied: principal {PrincipalId} attempted {Operation} on read-only public workspace")]
    public static partial void PublicWorkspaceMutationDenied(
        ILogger logger, string? principalId, OperatorOperation operation);
}
