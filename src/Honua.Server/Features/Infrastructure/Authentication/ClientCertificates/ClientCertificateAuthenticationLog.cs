// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal static partial class ClientCertificateAuthenticationLog
{
    [LoggerMessage(
        EventId = 4140,
        Level = LogLevel.Debug,
        Message = "Client certificate authentication succeeded for profile {ProfileId} in environment {EnvironmentId}.")]
    public static partial void AuthenticationSucceeded(ILogger logger, string? profileId, string? environmentId);

    [LoggerMessage(
        EventId = 4141,
        Level = LogLevel.Warning,
        Message = "Client certificate authentication failed with {ErrorCode} for profile {ProfileId} in environment {EnvironmentId}.")]
    public static partial void AuthenticationFailed(
        ILogger logger,
        string errorCode,
        string? profileId,
        string? environmentId);

    [LoggerMessage(
        EventId = 4142,
        Level = LogLevel.Warning,
        Message = "Client certificate for profile {ProfileId} in environment {EnvironmentId} expires in {DaysUntilExpiry} days.")]
    public static partial void CertificateExpirationWarning(
        ILogger logger,
        string? profileId,
        string? environmentId,
        int? daysUntilExpiry);

    [LoggerMessage(
        EventId = 4143,
        Level = LogLevel.Information,
        Message = "Client certificate trust profile {ProfileId} changed for environment {EnvironmentId}.")]
    public static partial void TrustProfileChanged(ILogger logger, string? profileId, string? environmentId);

    [LoggerMessage(
        EventId = 4144,
        Level = LogLevel.Information,
        Message = "Client certificate principal mapping {MappingId} changed for profile {ProfileId}.")]
    public static partial void MappingChanged(ILogger logger, string? profileId, string? mappingId);

    [LoggerMessage(
        EventId = 4145,
        Level = LogLevel.Information,
        Message = "Client certificate revocation list changed for profile {ProfileId}.")]
    public static partial void RevocationChanged(ILogger logger, string? profileId);
}
