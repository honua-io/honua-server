// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.Zarr;

/// <summary>
/// Structured logging for Zarr admin operations. Event ID range: 7920-7939.
/// </summary>
internal static partial class ZarrLog
{
    [LoggerMessage(
        EventId = 7920,
        Level = LogLevel.Information,
        Message = "Registered Zarr store '{Name}' (ID {RegistrationId}) for layer {LayerId}: {Provider}://{Bucket}/{RootPath}")]
    public static partial void ZarrRegistered(ILogger logger, string name, long registrationId, int layerId, string provider, string bucket, string rootPath);

    [LoggerMessage(
        EventId = 7921,
        Level = LogLevel.Information,
        Message = "Unregistered Zarr store {RegistrationId}")]
    public static partial void ZarrUnregistered(ILogger logger, long registrationId);

    [LoggerMessage(
        EventId = 7922,
        Level = LogLevel.Information,
        Message = "Listed {Count} Zarr registrations")]
    public static partial void ZarrListRetrieved(ILogger logger, int count);

    [LoggerMessage(
        EventId = 7923,
        Level = LogLevel.Information,
        Message = "Zarr metadata scan started for {RegistrationId}: {Provider}://{Bucket}/{RootPath}")]
    public static partial void MetadataScanStarted(ILogger logger, long registrationId, string provider, string bucket, string rootPath);

    [LoggerMessage(
        EventId = 7924,
        Level = LogLevel.Information,
        Message = "Zarr metadata scan completed for {RegistrationId}: {VariableCount} variables, SRID {Srid}")]
    public static partial void MetadataScanCompleted(ILogger logger, long registrationId, int variableCount, int srid);

    [LoggerMessage(
        EventId = 7925,
        Level = LogLevel.Error,
        Message = "Zarr metadata scan failed for {RegistrationId}")]
    public static partial void MetadataScanFailed(ILogger logger, Exception ex, long registrationId);
}
