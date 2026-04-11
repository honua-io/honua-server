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
    Job
}
