// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Source-generated log messages for the scoped-job token issuer (custom-code auth
/// spine, Phase 0). Tokens are never written verbatim; an 8-character SHA-256
/// prefix is emitted instead so issuance and validation events can be correlated
/// without leaking the secret value to log sinks.
/// </summary>
internal static partial class ScopedJobTokenLog
{
    [LoggerMessage(EventId = 7050, Level = LogLevel.Information,
        Message = "Scoped-job token issued for job {JobId} with frozen attenuation roles={RoleCount} permissions={PermissionCount}.")]
    public static partial void TokenIssued(ILogger logger, string jobId, int roleCount, int permissionCount);

    [LoggerMessage(EventId = 7051, Level = LogLevel.Warning,
        Message = "Scoped-job token validation rejected: token {TokenHash} was minted for job {BoundJobId} but presented in a different job context.")]
    public static partial void JobBindingMismatch(ILogger logger, string boundJobId, string tokenHash);

    [LoggerMessage(EventId = 7052, Level = LogLevel.Warning,
        Message = "Scoped-job token store could not persist distributed cache entry {KeyHash}.")]
    public static partial void DistributedCachePersistFailed(ILogger logger, string keyHash, Exception exception);

    [LoggerMessage(EventId = 7053, Level = LogLevel.Warning,
        Message = "Scoped-job token store could not read distributed cache entry {KeyHash}.")]
    public static partial void DistributedCacheReadFailed(ILogger logger, string keyHash, Exception exception);

    [LoggerMessage(EventId = 7054, Level = LogLevel.Warning,
        Message = "Scoped-job token store could not remove distributed cache entry {KeyHash}.")]
    public static partial void DistributedCacheRemoveFailed(ILogger logger, string keyHash, Exception exception);
}
