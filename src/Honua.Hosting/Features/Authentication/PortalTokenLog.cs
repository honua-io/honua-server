// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Source-generated log messages for the portal-token issuer and authentication
/// handler. Tokens are never written verbatim; an 8-character SHA-256 prefix is
/// emitted instead so support can correlate issuance and validation events without
/// the value leaking to log sinks.
/// </summary>
internal static partial class PortalTokenLog
{
    [LoggerMessage(EventId = 7001, Level = LogLevel.Information,
        Message = "Portal token issued for principal {PrincipalHash} (tenant {TenantHash}, client {ClientType}); expires {ExpiresAt:o}.")]
    public static partial void TokenIssued(
        ILogger logger,
        string principalHash,
        string tenantHash,
        string clientType,
        DateTimeOffset expiresAt);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning,
        Message = "Portal token issuance rejected: {Reason}.")]
    public static partial void TokenIssuanceRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning,
        Message = "Portal token validation rejected: token {TokenHash} binding {ClientType} did not match request binding.")]
    public static partial void BindingMismatch(ILogger logger, string clientType, string tokenHash);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Warning,
        Message = "Portal token store could not persist distributed cache entry {KeyHash}.")]
    public static partial void DistributedCachePersistFailed(ILogger logger, string keyHash, Exception exception);

    [LoggerMessage(EventId = 7005, Level = LogLevel.Warning,
        Message = "Portal token store could not read distributed cache entry {KeyHash}.")]
    public static partial void DistributedCacheReadFailed(ILogger logger, string keyHash, Exception exception);

    [LoggerMessage(EventId = 7006, Level = LogLevel.Warning,
        Message = "Portal token store could not remove distributed cache entry {KeyHash}.")]
    public static partial void DistributedCacheRemoveFailed(ILogger logger, string keyHash, Exception exception);
}
