namespace Honua.Core.Features.Authorization.Domain;

public readonly record struct OperatorScopeDecision
{
    private OperatorScopeDecision(bool isAllowed, bool isScopeGoverned, string? reason)
    {
        IsAllowed = isAllowed;
        IsScopeGoverned = isScopeGoverned;
        Reason = reason;
    }

    public bool IsAllowed { get; }

    public bool IsScopeGoverned { get; }

    public string? Reason { get; }

    public static OperatorScopeDecision NotGoverned() => new(true, false, null);

    public static OperatorScopeDecision Allowed() => new(true, true, null);

    public static OperatorScopeDecision Denied(string reason) => new(false, true, reason);
}
