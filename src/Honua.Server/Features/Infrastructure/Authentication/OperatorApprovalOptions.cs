namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Configuration options for operator approval policies.
/// </summary>
internal sealed class OperatorApprovalOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "OperatorApproval";

    /// <summary>
    /// Whether publish operations require human approval.
    /// </summary>
    public bool PublishRequiresApproval { get; set; } = true;

    /// <summary>
    /// Whether destructive actions require human approval.
    /// </summary>
    public bool DestructiveActionsRequireApproval { get; set; } = true;
}
