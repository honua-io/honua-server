// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.Studio.Abstractions;

/// <summary>
/// Studio package-lifecycle operations classified for end-user authorization
/// (honua-server#3001). Every operation targets either a resource the caller is creating
/// (<c>resourceOwnerId</c> is <see langword="null"/>) or an existing resource with a known
/// owner.
/// </summary>
public enum StudioAuthorizationOperation
{
    /// <summary>Create a new mutable package draft (or a new draft under an existing item).</summary>
    CreateDraft,

    /// <summary>Read a mutable package draft.</summary>
    ReadDraft,

    /// <summary>Replace a mutable package draft.</summary>
    UpdateDraft,

    /// <summary>Delete a mutable package draft.</summary>
    DeleteDraft,

    /// <summary>Validate a mutable package draft or build its preview plan.</summary>
    ValidateDraft,

    /// <summary>Save a mutable package draft as an immutable content version.</summary>
    CreateVersion,

    /// <summary>List Studio content items or package drafts (enumeration scoping).</summary>
    ListOwn,

    /// <summary>Read an immutable content version, a version comparison, or a version list.</summary>
    ReadContentItem,

    /// <summary>
    /// Reopen a published/current immutable version as a new mutable draft. Not treated as an
    /// elevated operation: it only creates a draft the caller will own, mirroring
    /// <see cref="CreateDraft"/>.
    /// </summary>
    ReopenVersion,

    /// <summary>
    /// Request publication of an immutable content version. Elevated (REQ-003): gated by a
    /// matching <c>StudioDraft</c> operator grant in addition to ownership.
    /// </summary>
    PublishRequest,

    /// <summary>
    /// Roll a content item's current/published pointer back to a prior immutable version.
    /// Elevated (REQ-003): gated by a matching <c>StudioDraft</c> operator grant in addition to
    /// ownership.
    /// </summary>
    Rollback,
}

/// <summary>
/// Outcome of a Studio package-lifecycle authorization check (honua-server#3001).
/// </summary>
public sealed record StudioAuthorizationDecision
{
    /// <summary>Whether the operation is authorized.</summary>
    public required bool IsAllowed { get; init; }

    /// <summary>
    /// True when the decision passed through the elevated (operator-grant-gated) tier rather
    /// than the baseline ownership tier. Used to distinguish an audited policy decision
    /// (REQ-003) from a routine ownership check.
    /// </summary>
    public bool IsElevated { get; init; }

    /// <summary>
    /// Machine-readable denial code (RFC 7807 <c>studioAuthorizationCode</c> extension, REQ-004).
    /// <see langword="null"/> when <see cref="IsAllowed"/> is <see langword="true"/>.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>Human-readable denial reason. <see langword="null"/> when allowed.</summary>
    public string? Reason { get; init; }

    /// <summary>Creates an allowed decision.</summary>
    public static StudioAuthorizationDecision Allow(bool elevated = false) => new()
    {
        IsAllowed = true,
        IsElevated = elevated,
    };

    /// <summary>Creates a denied decision with a machine-readable code and human-readable reason.</summary>
    public static StudioAuthorizationDecision Deny(string code, string reason, bool elevated = false) => new()
    {
        IsAllowed = false,
        IsElevated = elevated,
        Code = code,
        Reason = reason,
    };
}

/// <summary>
/// Studio package-lifecycle authorization policy layer (honua-server#3001). Classifies
/// operations into an ownership-scoped baseline tier (draft CRUD, version create, and reads on
/// the caller's own resources) and a policy-gated elevated tier (publish-request, rollback),
/// behind the <see cref="StudioEndUserAuthorizationOptions.Enabled"/> feature flag. Admin
/// principals always have full, unscoped access. Reuses the platform's existing
/// <see cref="Authorization.Abstractions.IOperatorAuthorizationEvaluator"/> role/grant
/// infrastructure for the elevated tier via
/// <see cref="Authorization.Domain.OperatorResourceType.StudioDraft"/> rather than
/// introducing a parallel authorization mechanism.
/// </summary>
public interface IStudioAuthorizationService
{
    /// <summary>
    /// Whether ownership-scoped, non-admin Studio authorization is enabled
    /// (<c>Studio:EndUserAuthorization:Enabled</c>). When <see langword="false"/>, callers
    /// should preserve the pre-#3001, admin-only posture unchanged (NFR-001).
    /// </summary>
    bool IsEndUserAuthorizationEnabled { get; }

    /// <summary>Returns true when the principal holds the platform <c>admin</c> role.</summary>
    bool IsAdmin(ClaimsPrincipal principal);

    /// <summary>
    /// Resolves the stable actor identifier used both as the ownership key and as the
    /// operator-authorization subject: <c>NameIdentifier</c>/<c>sub</c>, falling back to the
    /// admin API-key id/name claims, then <see cref="ClaimsIdentity.Name"/>. Mirrors
    /// <c>ConsolePrincipal.ResolveActorId</c> (duplicated here rather than referenced, because
    /// <c>Honua.Core</c> cannot depend on <c>Honua.Server</c>) so ownership recorded at create
    /// time and the caller id compared at authorization time always agree. Endpoint callers that
    /// already resolved the actor id (for example to stamp <c>ActorId</c> on a lifecycle command)
    /// should reuse that value as the <c>callerId</c> argument to
    /// <see cref="AuthorizeAsync"/> instead of resolving twice.
    /// </summary>
    string? ResolveCallerId(ClaimsPrincipal principal);

    /// <summary>
    /// Authorizes a Studio package-lifecycle operation.
    /// </summary>
    /// <param name="principal">The authenticated caller.</param>
    /// <param name="callerId">
    /// The caller's resolved actor id (see <see cref="ResolveCallerId"/>); callers that already
    /// resolved it for another purpose (for example to stamp a command's <c>ActorId</c>) should
    /// pass that value directly so both resolutions agree.
    /// </param>
    /// <param name="operation">The operation being performed.</param>
    /// <param name="resourceOwnerId">
    /// The owning principal id of the target resource (draft/content item), or
    /// <see langword="null"/> when creating a brand-new resource with no prior owner.
    /// </param>
    /// <param name="isPubliclyReadable">
    /// True when the target resource has a published version, admitting a read-only operation
    /// to any authenticated caller regardless of ownership. Ignored for non-read operations.
    /// </param>
    /// <param name="resourceId">
    /// The concrete resource id (draft or content item), used only for the elevated tier's
    /// operator-grant lookup so an operator-provisioned, resource-specific grant can authorize
    /// a delegate. May be <see langword="null"/> for baseline (non-elevated) operations.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StudioAuthorizationDecision> AuthorizeAsync(
        ClaimsPrincipal principal,
        string? callerId,
        StudioAuthorizationOperation operation,
        string? resourceOwnerId,
        bool isPubliclyReadable = false,
        string? resourceId = null,
        CancellationToken cancellationToken = default);
}
