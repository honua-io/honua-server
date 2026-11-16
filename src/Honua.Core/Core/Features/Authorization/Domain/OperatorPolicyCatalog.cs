namespace Honua.Core.Features.Authorization.Domain;

public sealed record OperatorPolicyCatalog
{
    public bool PublishRequiresApproval { get; init; }

    public bool DestructiveActionsRequireApproval { get; init; }

    public WorkspaceVisibility MaxWorkspaceVisibility { get; init; }

    public IReadOnlyDictionary<OperatorResourceType, IReadOnlyList<OperatorOperation>> AllowedOperations { get; init; }
        = new Dictionary<OperatorResourceType, IReadOnlyList<OperatorOperation>>();
}
