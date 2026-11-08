namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Scoping model for workspace and artifact access boundaries.
/// </summary>
public enum WorkspaceVisibility
{
    /// <summary>Visible only to the creating principal.</summary>
    Personal,

    /// <summary>Visible to principals sharing the same workspace scope claim.</summary>
    Shared,

    /// <summary>Visible to any authenticated principal in the organization.</summary>
    Organization,

    /// <summary>Read-only, no authentication required.</summary>
    Public
}
