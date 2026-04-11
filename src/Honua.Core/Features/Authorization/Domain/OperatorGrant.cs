namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// A generalized permission grant for operator-scoped resources.
/// Uses enums for resource type and operation (two integer comparisons on the hot path).
/// Only ResourceId is a string because resource identifiers are unbounded.
/// </summary>
public sealed record OperatorGrant
{
    /// <summary>
    /// The kind of resource this permission applies to.
    /// </summary>
    public required OperatorResourceType ResourceType { get; init; }

    /// <summary>
    /// The specific resource identifier, or "*" for a wildcard grant.
    /// </summary>
    public required string ResourceId { get; init; }

    /// <summary>
    /// The operation this permission authorizes.
    /// </summary>
    public required OperatorOperation Operation { get; init; }

    /// <summary>
    /// Wildcard resource identifier that matches all resources of a given type.
    /// </summary>
    public const string Wildcard = "*";
}
