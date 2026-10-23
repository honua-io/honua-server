// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.Collaboration.FeatureLocks;

/// <summary>
/// Outcome classes for a feature-lock authorization decision.
/// </summary>
public enum FeatureLockAuthorizationStatus
{
    /// <summary>The caller may perform the requested lock operation.</summary>
    Authorized,

    /// <summary>The caller must authenticate first.</summary>
    RequiresAuthentication,

    /// <summary>The caller is authenticated but not permitted.</summary>
    Forbidden
}

/// <summary>
/// The decision an <see cref="IFeatureLockAuthorizer"/> returns for one lock operation.
/// </summary>
public sealed record FeatureLockAuthorizationResult
{
    private FeatureLockAuthorizationResult(
        FeatureLockAuthorizationStatus status,
        FeatureLockAccessContext access,
        string? detail)
    {
        Status = status;
        Access = access;
        Detail = detail;
    }

    /// <summary>Gets the decision class.</summary>
    public FeatureLockAuthorizationStatus Status { get; }

    /// <summary>Gets the access context handed to the lock service when authorized.</summary>
    public FeatureLockAccessContext Access { get; }

    /// <summary>Gets an optional human-readable explanation surfaced to the caller.</summary>
    public string? Detail { get; }

    /// <summary>Gets a value indicating whether the operation may proceed.</summary>
    public bool Authorized => Status == FeatureLockAuthorizationStatus.Authorized;

    /// <summary>Allows the operation with write access.</summary>
    /// <returns>An authorized result.</returns>
    public static FeatureLockAuthorizationResult AllowWrite() =>
        new(FeatureLockAuthorizationStatus.Authorized, FeatureLockAccessContext.AuthorizedWrite, detail: null);

    /// <summary>Requires the caller to authenticate.</summary>
    /// <param name="detail">The explanation surfaced to the caller.</param>
    /// <returns>An unauthenticated result.</returns>
    public static FeatureLockAuthorizationResult RequireAuthentication(string detail) =>
        new(FeatureLockAuthorizationStatus.RequiresAuthentication, FeatureLockAccessContext.Unauthorized, detail);

    /// <summary>Denies the operation.</summary>
    /// <param name="detail">The explanation surfaced to the caller.</param>
    /// <returns>A forbidden result.</returns>
    public static FeatureLockAuthorizationResult Forbid(string detail) =>
        new(FeatureLockAuthorizationStatus.Forbidden, FeatureLockAccessContext.ReadOnly, detail);
}

/// <summary>
/// Decides whether a caller may claim, renew or release a lock on a feature of a saved map.
/// </summary>
/// <remarks>
/// <para>
/// This is the extension point a deployment must implement to turn collaborative editing
/// on. The implementation Honua ships (<c>FailClosedFeatureLockAuthorizer</c>) denies every
/// claim, because per-map and per-layer editing ACLs are not part of 2026.1 — so out of the
/// box no lease is ever granted. Register your own with
/// <c>services.AddSingleton&lt;IFeatureLockAuthorizer, MyAuthorizer&gt;()</c> before the
/// server's own registration runs, or replace the registered descriptor afterwards.
/// </para>
/// <para>
/// The contract is public so a downstream host or plugin assembly can implement it without
/// rebuilding the server: an <c>internal</c> seam would have made the documented
/// "supply your own authorizer" path impossible to take (#4402).
/// </para>
/// </remarks>
public interface IFeatureLockAuthorizer
{
    /// <summary>
    /// Authorizes one lock operation.
    /// </summary>
    /// <param name="mapId">The saved map the editing session belongs to.</param>
    /// <param name="feature">The feature the lease targets, in canonical form.</param>
    /// <param name="principal">The authenticated caller.</param>
    /// <param name="operation">One of <c>claim</c>, <c>renew</c> or <c>release</c>.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The authorization decision.</returns>
    ValueTask<FeatureLockAuthorizationResult> AuthorizeAsync(
        string mapId,
        FeatureRef feature,
        ClaimsPrincipal principal,
        string operation,
        CancellationToken cancellationToken);
}
