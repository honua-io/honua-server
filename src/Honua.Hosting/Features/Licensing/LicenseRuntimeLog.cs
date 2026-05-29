// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Server.Features.Infrastructure.Licensing;

internal static partial class LicenseRuntimeLog
{
    [LoggerMessage(
        EventId = 10000,
        Level = LogLevel.Information,
        Message = "No license path configured; running in Community mode.")]
    public static partial void NoLicensePathConfigured(ILogger logger);

    [LoggerMessage(
        EventId = 10001,
        Level = LogLevel.Warning,
        Message = "Configured license file is missing; running in Community mode. licensePath={LicensePath}")]
    public static partial void LicenseFileMissing(ILogger logger, string licensePath);

    [LoggerMessage(
        EventId = 10002,
        Level = LogLevel.Warning,
        Message = "License file is malformed; running in Community mode. reason={Reason}")]
    public static partial void LicenseMalformed(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 10003,
        Level = LogLevel.Warning,
        Message = "License references an unknown signing key; running in Community mode. keyId={KeyId}")]
    public static partial void UnknownKey(ILogger logger, string keyId);

    [LoggerMessage(
        EventId = 10004,
        Level = LogLevel.Warning,
        Message = "License signature validation failed; running in Community mode. keyId={KeyId}")]
    public static partial void InvalidSignature(ILogger logger, string keyId);

    [LoggerMessage(
        EventId = 10005,
        Level = LogLevel.Warning,
        Message = "License is expired; running in Community mode. licenseId={LicenseId} expiresAt={ExpiresAt}")]
    public static partial void LicenseExpired(ILogger logger, string? licenseId, DateTimeOffset? expiresAt);

    [LoggerMessage(
        EventId = 10006,
        Level = LogLevel.Information,
        Message = "License loaded successfully. licenseId={LicenseId} edition={Edition} keyId={KeyId} expiresAt={ExpiresAt} activeEntitlementCount={ActiveEntitlementCount}")]
    public static partial void LicenseLoaded(
        ILogger logger,
        string? licenseId,
        HonuaEdition edition,
        string keyId,
        DateTimeOffset? expiresAt,
        int activeEntitlementCount);

    [LoggerMessage(
        EventId = 10007,
        Level = LogLevel.Warning,
        Message = "License upload rejected. reason={Reason}")]
    public static partial void LicenseUploadRejected(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 10008,
        Level = LogLevel.Error,
        Message = "License upload could not be saved.")]
    public static partial void LicenseUploadSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 10009,
        Level = LogLevel.Warning,
        Message = "Entitlement denied. entitlementKey={EntitlementKey} featureName={FeatureName} edition={Edition} validationState={ValidationState}")]
    public static partial void EntitlementDenied(
        ILogger logger,
        string entitlementKey,
        string featureName,
        HonuaEdition edition,
        LicenseValidationState validationState);
}
