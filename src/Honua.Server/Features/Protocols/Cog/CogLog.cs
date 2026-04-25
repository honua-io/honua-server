// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.Cog;

/// <summary>
/// Structured logging for COG operations.
/// Event ID range: 7900-7999.
/// </summary>
internal static partial class CogLog
{
    [LoggerMessage(
        EventId = 7900,
        Level = LogLevel.Information,
        Message = "Registered COG '{Name}' (ID {RegistrationId}) for layer {LayerId}: {Provider}://{Bucket}/{ObjectKey}")]
    public static partial void CogRegistered(ILogger logger, string name, long registrationId, int layerId, string provider, string bucket, string objectKey);

    [LoggerMessage(
        EventId = 7901,
        Level = LogLevel.Information,
        Message = "Unregistered COG {RegistrationId}")]
    public static partial void CogUnregistered(ILogger logger, long registrationId);

    [LoggerMessage(
        EventId = 7902,
        Level = LogLevel.Information,
        Message = "Listed {Count} COG registrations")]
    public static partial void CogListRetrieved(ILogger logger, int count);

    [LoggerMessage(
        EventId = 7903,
        Level = LogLevel.Information,
        Message = "Metadata scan started for COG {RegistrationId}: {Provider}://{Bucket}/{ObjectKey}")]
    public static partial void MetadataScanStarted(ILogger logger, long registrationId, string provider, string bucket, string objectKey);

    [LoggerMessage(
        EventId = 7904,
        Level = LogLevel.Information,
        Message = "Metadata scan completed for COG {RegistrationId}: {Width}x{Height}, {BandCount} bands, {OverviewCount} overviews")]
    public static partial void MetadataScanCompleted(ILogger logger, long registrationId, int width, int height, int bandCount, int overviewCount);

    [LoggerMessage(
        EventId = 7905,
        Level = LogLevel.Error,
        Message = "Metadata scan failed for COG {RegistrationId}")]
    public static partial void MetadataScanFailed(ILogger logger, Exception ex, long registrationId);

    [LoggerMessage(
        EventId = 7906,
        Level = LogLevel.Information,
        Message = "COG tile served for registration {RegistrationId}: level={Level}, row={Row}, col={Col}, {DataSize} bytes (cache={CacheHit})")]
    public static partial void CogTileServed(ILogger logger, long registrationId, int level, int row, int col, int dataSize, string cacheHit);

    [LoggerMessage(
        EventId = 7907,
        Level = LogLevel.Debug,
        Message = "COG tile not found for registration {RegistrationId}: level={Level}, row={Row}, col={Col}")]
    public static partial void CogTileNotFound(ILogger logger, long registrationId, int level, int row, int col);

    [LoggerMessage(
        EventId = 7908,
        Level = LogLevel.Warning,
        Message = "Unsupported compression '{Compression}' in COG {RegistrationId}. JPEG passthrough and DEFLATE decompression are supported.")]
    public static partial void UnsupportedCompression(ILogger logger, string compression, long registrationId);

    [LoggerMessage(
        EventId = 7909,
        Level = LogLevel.Warning,
        Message = "COG {RegistrationId} uses CRS SRID {Srid} which is not EPSG:3857 or EPSG:4326. Web map clients may display tiles incorrectly.")]
    public static partial void NonWebMercatorCrs(ILogger logger, long registrationId, int srid);

    [LoggerMessage(
        EventId = 7910,
        Level = LogLevel.Debug,
        Message = "COG {RegistrationId} cannot satisfy requested format {RequestedFormat} with native tile content type {ContentType}.")]
    public static partial void UnsupportedTileFormat(ILogger logger, long registrationId, string requestedFormat, string contentType);
}
