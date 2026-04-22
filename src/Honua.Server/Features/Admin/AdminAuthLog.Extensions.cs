// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static partial class AdminAuthLog
{
    [LoggerMessage(EventId = 4007, Level = LogLevel.Warning, Message = "OIDC provider {ProviderKey} did not return an authorization endpoint.")]
    public static partial void MissingAuthorizationEndpoint(ILogger logger, string providerKey);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Warning, Message = "Failed to create authorize URL for provider {ProviderKey}.")]
    public static partial void CreateAuthorizeUrlFailed(ILogger logger, string providerKey, Exception exception);

    [LoggerMessage(EventId = 4009, Level = LogLevel.Warning, Message = "OIDC provider {ProviderKey} did not return a token endpoint.")]
    public static partial void MissingTokenEndpoint(ILogger logger, string providerKey);

    [LoggerMessage(EventId = 4010, Level = LogLevel.Warning, Message = "OIDC token request for provider {ProviderKey} failed with status code {StatusCode}.")]
    public static partial void TokenRequestFailed(ILogger logger, string providerKey, int statusCode);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Warning, Message = "OIDC token request for provider {ProviderKey} returned an empty token response.")]
    public static partial void EmptyTokenResponse(ILogger logger, string providerKey);

    [LoggerMessage(EventId = 4012, Level = LogLevel.Warning, Message = "OIDC token request for provider {ProviderKey} returned tokens that could not be projected into an admin session.")]
    public static partial void InvalidTokenProjection(ILogger logger, string providerKey);

    [LoggerMessage(EventId = 4013, Level = LogLevel.Warning, Message = "Failed to request token for provider {ProviderKey}.")]
    public static partial void RequestTokenFailed(ILogger logger, string providerKey, Exception exception);

    [LoggerMessage(EventId = 4014, Level = LogLevel.Warning, Message = "Failed to create logout URL for provider {ProviderKey}.")]
    public static partial void CreateLogoutUrlFailed(ILogger logger, string providerKey, Exception exception);
}
