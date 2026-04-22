// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static partial class RoleEndpoints
{
    internal static partial class RoleLog
    {
        [LoggerMessage(EventId = 4534, Level = LogLevel.Error, Message = "Failed to list roles")]
        public static partial void ListRolesFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4535, Level = LogLevel.Error, Message = "Failed to create role")]
        public static partial void CreateRoleFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4536, Level = LogLevel.Error, Message = "Failed to get role {RoleId}")]
        public static partial void GetRoleFailed(ILogger logger, Guid roleId, Exception exception);

        [LoggerMessage(EventId = 4537, Level = LogLevel.Error, Message = "Failed to update role {RoleId}")]
        public static partial void UpdateRoleFailed(ILogger logger, Guid roleId, Exception exception);

        [LoggerMessage(EventId = 4538, Level = LogLevel.Error, Message = "Failed to delete role {RoleId}")]
        public static partial void DeleteRoleFailed(ILogger logger, Guid roleId, Exception exception);

        [LoggerMessage(EventId = 4539, Level = LogLevel.Error, Message = "Failed to get permissions for role {RoleId}")]
        public static partial void GetPermissionsFailed(ILogger logger, Guid roleId, Exception exception);

        [LoggerMessage(EventId = 4540, Level = LogLevel.Error, Message = "Failed to set permissions for role {RoleId}")]
        public static partial void SetPermissionsFailed(ILogger logger, Guid roleId, Exception exception);
    }
}
