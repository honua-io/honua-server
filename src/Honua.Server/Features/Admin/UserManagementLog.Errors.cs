// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static partial class UserManagementEndpoints
{
    internal static partial class UserManagementLog
    {
        [LoggerMessage(EventId = 4524, Level = LogLevel.Error, Message = "Failed to list users")]
        public static partial void ListUsersFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4525, Level = LogLevel.Error, Message = "Failed to get user {UserId}")]
        public static partial void GetUserFailed(ILogger logger, string userId, Exception exception);

        [LoggerMessage(EventId = 4526, Level = LogLevel.Error, Message = "Failed to update roles for user {UserId}")]
        public static partial void UpdateUserRolesFailed(ILogger logger, string userId, Exception exception);

        [LoggerMessage(EventId = 4527, Level = LogLevel.Error, Message = "Failed to deprovision user {UserId}")]
        public static partial void DeprovisionUserFailed(ILogger logger, string userId, Exception exception);

        [LoggerMessage(EventId = 4528, Level = LogLevel.Error, Message = "Failed to resolve effective permissions for user {UserId}")]
        public static partial void ResolveEffectivePermissionsFailed(ILogger logger, string userId, Exception exception);
    }
}
