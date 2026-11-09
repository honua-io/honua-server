namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Result of intersecting an OAuth bearer token's scopes with a requested operator
/// operation (honua-server#2851). Distinct from the grant decision so a scope denial can be
/// reported as its own structured reason rather than as a generic permission denial.
/// </summary>
public readonly record struct OperatorScopeDecision
{
    private OperatorScopeDecision(bool isAllowed, bool isScopeGoverned, string? reason)
    {
        IsAllowed = isAllowed;
        IsScopeGoverned = isScopeGoverned;
        Reason = reason;
    }

    /// <summary>Whether the scope check permits the requested operation.</summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// Whether the principal is subject to scope governance at all. False for non-OAuth
    /// principals (X-API-Key, interactive sessions), which scope checks never narrow.
    /// </summary>
    public bool IsScopeGoverned { get; }

    /// <summary>Human-readable denial reason when <see cref="IsAllowed"/> is false; otherwise null.</summary>
    public string? Reason { get; }

    /// <summary>
    /// The principal is not scope-governed, so scopes impose no narrowing. Treated as allowed.
    /// </summary>
    public static OperatorScopeDecision NotGoverned() => new(true, false, null);

    /// <summary>The token's scopes permit the requested operation.</summary>
    public static OperatorScopeDecision Allowed() => new(true, true, null);

    /// <summary>The token's scopes do not permit the requested operation (fail-closed).</summary>
    public static OperatorScopeDecision Denied(string reason) => new(false, true, reason);
}
