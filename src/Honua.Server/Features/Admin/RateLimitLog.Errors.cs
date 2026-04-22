// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static partial class RateLimitEndpoints
{
    internal static partial class RateLimitLog
    {
        [LoggerMessage(EventId = 4543, Level = LogLevel.Error, Message = "Failed to list rate limit policies")]
        public static partial void ListPoliciesFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4544, Level = LogLevel.Error, Message = "Failed to create rate limit policy")]
        public static partial void CreatePolicyFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4545, Level = LogLevel.Error, Message = "Failed to get rate limit policy {PolicyId}")]
        public static partial void GetPolicyFailed(ILogger logger, Guid policyId, Exception exception);

        [LoggerMessage(EventId = 4546, Level = LogLevel.Error, Message = "Failed to update rate limit policy {PolicyId}")]
        public static partial void UpdatePolicyFailed(ILogger logger, Guid policyId, Exception exception);

        [LoggerMessage(EventId = 4547, Level = LogLevel.Error, Message = "Failed to delete rate limit policy {PolicyId}")]
        public static partial void DeletePolicyFailed(ILogger logger, Guid policyId, Exception exception);

        [LoggerMessage(EventId = 4548, Level = LogLevel.Error, Message = "Failed to get rate limit status for key '{Key}'")]
        public static partial void GetStatusFailed(ILogger logger, string? key, Exception exception);
    }
}
