// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Identity.Domain;

/// <summary>
/// A group provisioned via SCIM 2.0 (#510). Each group maps to a Honua role of the same
/// name; membership in the group confers the mapped role on the member user.
/// </summary>
public sealed class ScimGroup
{
    /// <summary>
    /// Stable unique identifier for the group (server-assigned).
    /// </summary>
    public required string GroupId { get; init; }

    /// <summary>
    /// SCIM <c>displayName</c>; also the Honua role name the group maps to.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// User identifiers that are members of this group.
    /// </summary>
    public IReadOnlyList<string> MemberUserIds { get; init; } = [];

    /// <summary>
    /// When the group was first provisioned.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the group record was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
