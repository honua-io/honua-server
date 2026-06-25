// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Identity.Scim;

/// <summary>
/// Source-generated structured logging for the SCIM 2.0 provisioning endpoints (#510).
/// User and group identifiers are stable, non-secret provisioning keys safe to log for audit.
/// </summary>
internal static partial class ScimLog
{
    [LoggerMessage(EventId = 4600, Level = LogLevel.Information,
        Message = "SCIM listed {Count} users (total: {TotalCount}).")]
    public static partial void UsersListed(ILogger logger, int count, int totalCount);

    [LoggerMessage(EventId = 4601, Level = LogLevel.Information,
        Message = "SCIM provisioned user '{UserId}'.")]
    public static partial void UserProvisioned(ILogger logger, string userId);

    [LoggerMessage(EventId = 4602, Level = LogLevel.Information,
        Message = "SCIM replaced user '{UserId}'.")]
    public static partial void UserReplaced(ILogger logger, string userId);

    [LoggerMessage(EventId = 4603, Level = LogLevel.Information,
        Message = "SCIM set user '{UserId}' active state to {Active}.")]
    public static partial void UserActiveChanged(ILogger logger, string userId, bool active);

    [LoggerMessage(EventId = 4604, Level = LogLevel.Information,
        Message = "SCIM deprovisioned user '{UserId}'.")]
    public static partial void UserDeprovisioned(ILogger logger, string userId);

    [LoggerMessage(EventId = 4605, Level = LogLevel.Information,
        Message = "SCIM provisioned group '{DisplayName}'.")]
    public static partial void GroupProvisioned(ILogger logger, string displayName);

    [LoggerMessage(EventId = 4606, Level = LogLevel.Information,
        Message = "SCIM replaced group '{DisplayName}'.")]
    public static partial void GroupReplaced(ILogger logger, string displayName);

    [LoggerMessage(EventId = 4607, Level = LogLevel.Information,
        Message = "SCIM updated group '{DisplayName}' membership (+{Added}/-{Removed}).")]
    public static partial void GroupMembersChanged(ILogger logger, string displayName, int added, int removed);

    [LoggerMessage(EventId = 4608, Level = LogLevel.Information,
        Message = "SCIM deleted group '{GroupId}'.")]
    public static partial void GroupDeleted(ILogger logger, string groupId);
}
