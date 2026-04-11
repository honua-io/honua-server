namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Transport-neutral input for operator authorization evaluation.
/// </summary>
public sealed record OperatorAuthorizationRequest
{
    /// <summary>
    /// The kind of resource being accessed.
    /// </summary>
    public required OperatorResourceType ResourceType { get; init; }

    /// <summary>
    /// The specific resource identifier, or null for type-level checks.
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// The operation being performed.
    /// </summary>
    public required OperatorOperation Operation { get; init; }

    /// <summary>
    /// Workspace visibility scope, when the request targets a workspace resource.
    /// </summary>
    public WorkspaceVisibility? WorkspaceVisibility { get; init; }

    /// <summary>
    /// The owner principal identifier for personal workspace checks.
    /// </summary>
    public string? WorkspaceOwnerId { get; init; }

    /// <summary>
    /// The scope group identifier for shared workspace checks.
    /// </summary>
    public string? WorkspaceScopeId { get; init; }

    /// <summary>
    /// Whether the requested action is destructive. Used by approval evaluation
    /// to distinguish destructive operations from routine execution.
    /// </summary>
    public bool IsDestructive { get; init; }
}
