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

    /// <summary>
    /// Emitted when a <c>generateToken</c> request carries credentials via the URL query
    /// string rather than a POST form body. The password value is never included in the
    /// log line; this entry is an audit signal for operators monitoring for insecure
    /// credential transmission patterns.
    /// </summary>
    [LoggerMessage(EventId = 7007, Level = LogLevel.Warning,
        Message = "Portal token request received credentials via URL query string; " +
                  "POST with a form-encoded body is preferred to avoid credential exposure " +
                  "in HTTP-layer logs.")]
    public static partial void CredentialsFromQueryString(ILogger logger);

    /// <summary>
    /// Emitted when an otherwise-valid token is refused because its tenant was synthesized by
    /// a claims-mapping entry whose Enterprise entitlement is no longer active. Distinct from
    /// <see cref="BindingMismatch"/> on purpose: this is a licensing/provenance refusal, not a
    /// referer/IP mismatch, and conflating them sends an operator diagnosing a sudden 401 wave
    /// after a licence lapse to entirely the wrong place (honua-server#2997 review).
    /// </summary>
    [LoggerMessage(EventId = 7008, Level = LogLevel.Warning,
        Message = "Portal token {TokenHash} refused: its tenant was derived from claims mapping " +
                  "and the identity.claims-mapping entitlement is no longer active.")]
    public static partial void ClaimsMappingTenantNoLongerEntitled(ILogger logger, string tokenHash);
}
