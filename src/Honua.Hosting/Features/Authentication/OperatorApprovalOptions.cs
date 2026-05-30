namespace Honua.Infrastructure.Authentication;

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

    /// <summary>
    /// Whether principals with the admin role bypass operator approval gates.
    /// Defaults to true so that admin-only endpoints (already behind RequireAdminAuthorization)
    /// are not double-gated by operator approval policies targeting non-admin operators.
    /// Set to false to require approval even for admin principals.
    /// </summary>
    public bool AdminExemptFromApproval { get; set; } = true;
}
