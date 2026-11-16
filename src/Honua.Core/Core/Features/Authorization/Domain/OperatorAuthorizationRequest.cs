namespace Honua.Core.Features.Authorization.Domain;

public sealed record OperatorAuthorizationRequest
{
    public required OperatorResourceType ResourceType { get; init; }

    public string? ResourceId { get; init; }

    public required OperatorOperation Operation { get; init; }

    public WorkspaceVisibility? WorkspaceVisibility { get; init; }

    public string? WorkspaceOwnerId { get; init; }

    public string? WorkspaceScopeId { get; init; }

    public bool IsDestructive { get; init; }

    public bool RequiresExplicitApproval { get; init; }

    public string? ApprovalPolicyRef { get; init; }

    public IReadOnlyList<string> ApprovalReasonCodes { get; init; } = Array.Empty<string>();
}
