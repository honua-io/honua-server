namespace Honua.Infrastructure.Authentication;

internal sealed class OperatorApprovalOptions
{
    public const string SectionName = "OperatorApproval";

    public bool PublishRequiresApproval { get; set; } = true;

    public bool DestructiveActionsRequireApproval { get; set; } = true;

    public bool AdminExemptFromApproval { get; set; } = true;
}
