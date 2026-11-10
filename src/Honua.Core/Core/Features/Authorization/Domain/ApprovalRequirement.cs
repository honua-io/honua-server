namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Result of approval evaluation indicating whether an authorized action
/// additionally requires human sign-off before execution.
/// </summary>
public sealed record ApprovalRequirement
{
    /// <summary>
    /// Whether approval is required before the action can proceed.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>
    /// Reference to the policy that triggered the approval requirement.
    /// </summary>
    public string? PolicyRef { get; init; }

    /// <summary>
    /// Machine-readable reason codes explaining why approval is required.
    /// </summary>
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];

    /// <summary>
    /// Creates a result indicating no approval is needed.
    /// </summary>
    public static ApprovalRequirement NotRequired() => new() { IsRequired = false };

    /// <summary>
    /// Creates a result indicating approval is required with the given policy and reasons.
    /// </summary>
    public static ApprovalRequirement Required(string policyRef, params string[] reasons) => new()
    {
        IsRequired = true,
        PolicyRef = policyRef,
        ReasonCodes = reasons
    };
}
