namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Identifies the operation being performed on an operator-scoped resource.
/// </summary>
public enum OperatorOperation
{
    /// <summary>Catalog and capability browsing.</summary>
    Discover,

    /// <summary>Read access to resource state or results.</summary>
    Read,

    /// <summary>Create a new resource or artifact.</summary>
    Create,

    /// <summary>Execute a process, run a job, or perform a stateful action.</summary>
    Execute,

    /// <summary>Promote a workspace artifact to a wider visibility scope.</summary>
    Promote,

    /// <summary>Deploy a package to a routable surface.</summary>
    Publish
}
