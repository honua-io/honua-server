namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Pre-evaluated permission and policy summary returned during operator grounding
/// so the planner knows permission boundaries before compiling a plan.
/// </summary>
public sealed record OperatorPolicyCatalog
{
    /// <summary>
    /// Whether publish operations require human approval.
    /// </summary>
    public bool PublishRequiresApproval { get; init; }

    /// <summary>
    /// Whether destructive actions require human approval.
    /// </summary>
    public bool DestructiveActionsRequireApproval { get; init; }

    /// <summary>
    /// The maximum workspace visibility the principal may use.
    /// </summary>
    public WorkspaceVisibility MaxWorkspaceVisibility { get; init; }

    /// <summary>
    /// Allowed operations per resource type, pre-evaluated from effective permissions.
    /// </summary>
    public IReadOnlyDictionary<OperatorResourceType, IReadOnlyList<OperatorOperation>> AllowedOperations { get; init; }
        = new Dictionary<OperatorResourceType, IReadOnlyList<OperatorOperation>>();
}
