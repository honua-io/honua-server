// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// The outcome of a per-operation permission resolution.
/// </summary>
public enum PermissionResult
{
    /// <summary>
    /// No grant in the principal's effective permissions covers the requested
    /// <c>(service, layer, operation)</c> tuple. Callers decide whether to fall
    /// back to a coarser policy or treat this as a denial.
    /// </summary>
    NoMatchingGrant = 0,

    /// <summary>
    /// A grant explicitly authorizes the requested operation.
    /// </summary>
    Allow = 1,

    /// <summary>
    /// Authentication is required before a decision can be made (the principal
    /// is anonymous and no anonymous grant applies).
    /// </summary>
    RequiresAuthentication = 2,
}

/// <summary>
/// The result of resolving a per-operation permission for a principal against
/// the <c>(service, layer, operation)</c> grant tuples in their
/// <see cref="EffectivePermissions"/> (#1375).
/// </summary>
public readonly record struct PermissionDecision
{
    private PermissionDecision(PermissionResult result, PermissionGrant? matchedGrant)
    {
        Result = result;
        MatchedGrant = matchedGrant;
    }

    /// <summary>
    /// The resolution outcome.
    /// </summary>
    public PermissionResult Result { get; }

    /// <summary>
    /// The grant that satisfied the request, when <see cref="Result"/> is
    /// <see cref="PermissionResult.Allow"/>; otherwise <see langword="null"/>.
    /// </summary>
    public PermissionGrant? MatchedGrant { get; }

    /// <summary>
    /// <see langword="true"/> when the operation is explicitly allowed.
    /// </summary>
    public bool IsAllowed => Result == PermissionResult.Allow;

    /// <summary>
    /// <see langword="true"/> when no grant matched, so the caller may fall back
    /// to a coarser policy (e.g. the legacy <c>AccessPolicy</c>).
    /// </summary>
    public bool HasNoMatchingGrant => Result == PermissionResult.NoMatchingGrant;

    /// <summary>
    /// Creates an allow decision backed by the grant that matched.
    /// </summary>
    /// <param name="matchedGrant">The grant that satisfied the request.</param>
    /// <returns>An allow decision.</returns>
    public static PermissionDecision Allow(PermissionGrant matchedGrant)
        => new(PermissionResult.Allow, matchedGrant);

    /// <summary>
    /// Creates a decision indicating no grant matched the request.
    /// </summary>
    /// <returns>A no-matching-grant decision.</returns>
    public static PermissionDecision NoMatch()
        => new(PermissionResult.NoMatchingGrant, null);

    /// <summary>
    /// Creates a decision indicating authentication is required.
    /// </summary>
    /// <returns>A requires-authentication decision.</returns>
    public static PermissionDecision RequiresAuthentication()
        => new(PermissionResult.RequiresAuthentication, null);
}
