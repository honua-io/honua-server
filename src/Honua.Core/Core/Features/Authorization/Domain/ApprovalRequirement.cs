namespace Honua.Core.Features.Authorization.Domain;

public sealed record ApprovalRequirement
{
    public bool IsRequired { get; init; }

    public string? PolicyRef { get; init; }

    public IReadOnlyList<string> ReasonCodes { get; init; } = [];

    public static ApprovalRequirement NotRequired() => new() { IsRequired = false };

    public static ApprovalRequirement Required(string policyRef, params string[] reasons) => new()
    {
        IsRequired = true,
        PolicyRef = policyRef,
        ReasonCodes = reasons
    };
}
