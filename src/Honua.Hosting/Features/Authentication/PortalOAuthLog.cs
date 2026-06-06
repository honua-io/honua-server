// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Source-generated log messages for the ArcGIS OAuth2 named-user bridge (#1242).
/// Codes and tokens are never written verbatim; an 8-character SHA-256 prefix is
/// emitted for cache keys instead so support can correlate events without the
/// value leaking to log sinks.
/// </summary>
internal static partial class PortalOAuthLog
{
    [LoggerMessage(EventId = 7101, Level = LogLevel.Warning,
        Message = "Portal OAuth store could not persist distributed cache entry {KeyHash}.")]
    public static partial void StorePersistFailed(ILogger logger, string keyHash, Exception exception);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Warning,
        Message = "Portal OAuth store could not read distributed cache entry {KeyHash}.")]
    public static partial void StoreReadFailed(ILogger logger, string keyHash, Exception exception);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Warning,
        Message = "Portal OAuth store could not remove distributed cache entry {KeyHash}.")]
    public static partial void StoreRemoveFailed(ILogger logger, string keyHash, Exception exception);

    [LoggerMessage(EventId = 7104, Level = LogLevel.Warning,
        Message = "Portal OAuth authorize rejected: {Reason}.")]
    public static partial void AuthorizeRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 7105, Level = LogLevel.Warning,
        Message = "Portal OAuth callback rejected: {Reason}.")]
    public static partial void CallbackRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 7106, Level = LogLevel.Warning,
        Message = "Portal OAuth token exchange rejected: {Reason}.")]
    public static partial void TokenRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 7107, Level = LogLevel.Warning,
        Message = "Portal OAuth identity provider unavailable for provider {ProviderKey}.")]
    public static partial void ProviderUnavailable(ILogger logger, string providerKey, Exception exception);

    [LoggerMessage(EventId = 7108, Level = LogLevel.Information,
        Message = "Portal OAuth authorization code issued for principal {PrincipalHash} (provider {ProviderKey}).")]
    public static partial void CodeIssued(ILogger logger, string principalHash, string providerKey);
}
