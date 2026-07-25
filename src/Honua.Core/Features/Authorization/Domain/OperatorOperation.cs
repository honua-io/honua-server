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

    /// <summary>
    /// Submit an operator-supplied custom-code geoprocessing job (arbitrary code
    /// fetched from an allowlisted repository and run in the custom-code Batch
    /// container). This is a strictly higher-privilege operation than
    /// <see cref="Execute"/>: a plain <c>Process.Execute</c> grant authorizes the
    /// built-in tool catalog only, whereas custom-code submission requires this
    /// dedicated grant (or the admin role) so a baseline execute caller cannot run
    /// operator-supplied code. See honua-server#2752.
    /// </summary>
    ExecuteCustomCode,

    /// <summary>
    /// Execute a built-in geoprocessing tool that mutates catalog data, imports a dataset,
    /// or writes to a caller-selected sink. This is additive to <see cref="Execute"/> so a
    /// baseline analytic-process role cannot invoke durable side effects.
    /// </summary>
    ExecuteMutatingProcess,

    /// <summary>Promote a workspace artifact to a wider visibility scope.</summary>
    Promote,

    /// <summary>Deploy a package to a routable surface.</summary>
    Publish,

    /// <summary>
    /// Revert a resource's current/published pointer to a prior immutable version
    /// (honua-server#3001). Distinct from <see cref="Publish"/> so an operator can grant
    /// publish-request rights without also granting rollback, and vice versa.
    /// </summary>
    Rollback,

    /// <summary>Modify an existing resource in place (as opposed to <see cref="Create"/>).</summary>
    Update,

    /// <summary>Remove a resource.</summary>
    Delete
}
