namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Identifies the kind of operator-scoped resource being accessed.
/// </summary>
public enum OperatorResourceType
{
    /// <summary>Service and layer catalog browsing for operator grounding.</summary>
    Catalog,

    /// <summary>Operator workspace containing artifacts and scratch state.</summary>
    Workspace,

    /// <summary>Deterministic process definition and execution context.</summary>
    Process,

    /// <summary>Map or app package artifact (covers both MapPackage and AppPackage).</summary>
    Package,

    /// <summary>Deployment target for publishing packages to routable surfaces.</summary>
    Deployment,

    /// <summary>Background execution job.</summary>
    Job,

    /// <summary>Published hosted service produced by the promotion lifecycle.</summary>
    PublishedService,

    /// <summary>
    /// Studio package lifecycle draft (map/app-family composition state; AD-8,
    /// honua-server#3002). A distinct grant family ("studio-compose") from
    /// <see cref="Package"/> so the operator-grant model can scope Studio draft
    /// lifecycle/composition tools independently of the existing package-review
    /// and authoring grants — #3001 widens this beyond the admin-tier default to
    /// ownership-scoped end users without touching unrelated package grants.
    /// </summary>
    StudioDraft
}
