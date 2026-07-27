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
    /// Studio package lifecycle draft (map/app-family composition state; honua-server#3001/#3002).
    /// A distinct grant family ("studio-compose") from <see cref="Package"/> so the
    /// operator-grant model can scope Studio draft lifecycle/composition tools independently
    /// of the existing package-review and authoring grants.
    /// </summary>
    /// <remarks>
    /// End-user ownership scoping (honua-server#3001): callers evaluating a <see cref="StudioDraft"/>
    /// request for a resource the caller owns should pass the literal sentinel resource id
    /// <c>"own"</c> (see <c>StudioAuthorizationService</c>) rather than the concrete draft/item
    /// guid. This lets an operator provision a single self-service grant
    /// (<c>Service=StudioDraft, Layer=own, Operation=...</c>) that authorizes every draft a
    /// non-admin principal owns, without pre-provisioning a grant per draft id. A grant scoped to
    /// a concrete resource id (or the <c>"*"</c> wildcard) continues to work unchanged for
    /// operator-provisioned cross-user/platform-wide access.
    /// </remarks>
    StudioDraft,
}
