// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

// Tokens are never logged verbatim; an eight-character SHA-256 prefix is emitted instead.
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
        Message = "Portal token validation rejected: token {TokenHash} binding {ClientType} did not match request binding " +
                  "(present {RequestBindingPresent}, issued {IssuedBindingHash}, request {RequestBindingHash}).")]
    public static partial void BindingMismatch(
        ILogger logger,
        string clientType,
        string tokenHash,
        bool requestBindingPresent,
        string issuedBindingHash,
        string requestBindingHash);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Warning,
        Message = "Portal token store could not persist distributed cache entry {KeyHash}.")]
    public static partial void DistributedCachePersistFailed(ILogger logger, string keyHash, Exception exception);

    [LoggerMessage(EventId = 7005, Level = LogLevel.Warning,
        Message = "Portal token store could not read distributed cache entry {KeyHash}.")]
    public static partial void DistributedCacheReadFailed(ILogger logger, string keyHash, Exception exception);

    [LoggerMessage(EventId = 7006, Level = LogLevel.Warning,
        Message = "Portal token store could not remove distributed cache entry {KeyHash}.")]
    public static partial void DistributedCacheRemoveFailed(ILogger logger, string keyHash, Exception exception);

    // This query-string credential warning never includes the password.
    [LoggerMessage(EventId = 7007, Level = LogLevel.Warning,
        Message = "Portal token request received credentials via URL query string; " +
                  "POST with a form-encoded body is preferred to avoid credential exposure " +
                  "in HTTP-layer logs.")]
    public static partial void CredentialsFromQueryString(ILogger logger);

    [LoggerMessage(EventId = 7008, Level = LogLevel.Warning,
        Message = "Portal token {TokenHash} refused: its tenant was derived from claims mapping " +
                  "and the identity.claims-mapping entitlement is no longer active.")]
    public static partial void ClaimsMappingTenantNoLongerEntitled(ILogger logger, string tokenHash);

    [LoggerMessage(EventId = 7009, Level = LogLevel.Warning,
        Message = "Portal token {TokenHash} refused: the credential it was issued from is no " +
                  "longer valid, or its provenance could not be confirmed.")]
    public static partial void SourceCredentialNoLongerValid(ILogger logger, string tokenHash);

    // An administrative API key in the query string is refused and its value is not logged.
    [LoggerMessage(EventId = 7011, Level = LogLevel.Warning,
        Message = "API key with administrative authority refused: it was presented in the token " +
                  "query parameter. Send administrative keys in the X-API-Key header.")]
    public static partial void QueryStringAdminApiKeyRefused(ILogger logger);
}
