// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Studio;

/// <summary>
/// Config-bound feature flag that widens Studio package-lifecycle authorization beyond
/// admin-only to ownership-scoped end users (honua-server#3001). Bound from the
/// <c>Studio:EndUserAuthorization</c> section (environment-variable form:
/// <c>Studio__EndUserAuthorization__Enabled=true</c>).
/// </summary>
/// <remarks>
/// <para>
/// Default is <see langword="false"/> so every existing admin-only client is byte-for-byte
/// unchanged (NFR-001): the Studio package lifecycle endpoint group keeps requiring the
/// <c>admin</c> role, <c>StudioAuthorizationService</c> short-circuits to admin-only,
/// and content-item/package-draft enumeration is unscoped exactly as it is today.
/// </para>
/// <para>
/// When <c>true</c>, authenticated non-admin principals can create/read/update/delete their
/// own Studio package drafts, save their own drafts as immutable versions, and read their own
/// or published content items; publish-request and rollback additionally require a matching
/// <see cref="Honua.Core.Features.Authorization.Domain.OperatorResourceType.StudioDraft"/>
/// operator grant (policy-gated elevated operations, REQ-003). Admins retain full,
/// unscoped access in both states.
/// </para>
/// </remarks>
public sealed class StudioEndUserAuthorizationOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Studio:EndUserAuthorization";

    /// <summary>
    /// Whether ownership-scoped, non-admin Studio package lifecycle authorization is enabled.
    /// Defaults to <see langword="false"/> (admin-only, matching pre-#3001 behavior).
    /// </summary>
    public bool Enabled { get; set; }
}
