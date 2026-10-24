// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static partial class OidcProviderEndpoints
{
    internal static partial class OidcProviderLog
    {
        [LoggerMessage(EventId = 4564, Level = LogLevel.Error, Message = "Failed to list OIDC providers")]
        public static partial void ListProvidersFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4565, Level = LogLevel.Error, Message = "Failed to create OIDC provider")]
        public static partial void CreateProviderFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4566, Level = LogLevel.Error, Message = "Failed to get OIDC provider {ProviderId}")]
        public static partial void GetProviderFailed(ILogger logger, Guid providerId, Exception exception);

        [LoggerMessage(EventId = 4567, Level = LogLevel.Error, Message = "Failed to update OIDC provider {ProviderId}")]
        public static partial void UpdateProviderFailed(ILogger logger, Guid providerId, Exception exception);

        [LoggerMessage(EventId = 4568, Level = LogLevel.Error, Message = "Failed to delete OIDC provider {ProviderId}")]
        public static partial void DeleteProviderFailed(ILogger logger, Guid providerId, Exception exception);

        [LoggerMessage(EventId = 4569, Level = LogLevel.Error, Message = "Failed to test OIDC provider {ProviderId}")]
        public static partial void TestProviderFailed(ILogger logger, Guid providerId, Exception exception);
    }
}
