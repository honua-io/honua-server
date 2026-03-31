// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.CloudCog;

/// <summary>
/// Structured logging for Cloud COG operations.
/// Event ID range: 7900-7999.
/// </summary>
internal static partial class CloudCogLog
{
    [LoggerMessage(
        EventId = 7900,
        Level = LogLevel.Information,
        Message = "Registered cloud COG '{Name}' (ID {RegistrationId}) for layer {LayerId}: {Provider}://{Bucket}/{ObjectKey}")]
    public static partial void CogRegistered(ILogger logger, string name, long registrationId, int layerId, string provider, string bucket, string objectKey);

    [LoggerMessage(
        EventId = 7901,
        Level = LogLevel.Information,
        Message = "Unregistered cloud COG {RegistrationId}")]
    public static partial void CogUnregistered(ILogger logger, long registrationId);

    [LoggerMessage(
        EventId = 7902,
        Level = LogLevel.Information,
        Message = "Listed {Count} cloud COG registrations")]
    public static partial void CogListRetrieved(ILogger logger, int count);

    [LoggerMessage(
        EventId = 7903,
        Level = LogLevel.Information,
        Message = "Metadata scan started for cloud COG {RegistrationId}: {Provider}://{Bucket}/{ObjectKey}")]
    public static partial void MetadataScanStarted(ILogger logger, long registrationId, string provider, string bucket, string objectKey);

    [LoggerMessage(
        EventId = 7904,
        Level = LogLevel.Information,
        Message = "Metadata scan completed for cloud COG {RegistrationId}: {Width}x{Height}, {BandCount} bands, {OverviewCount} overviews")]
    public static partial void MetadataScanCompleted(ILogger logger, long registrationId, int width, int height, int bandCount, int overviewCount);

    [LoggerMessage(
        EventId = 7905,
        Level = LogLevel.Error,
        Message = "Metadata scan failed for cloud COG {RegistrationId}")]
    public static partial void MetadataScanFailed(ILogger logger, Exception ex, long registrationId);

    [LoggerMessage(
        EventId = 7906,
        Level = LogLevel.Information,
        Message = "Cloud COG tile served for registration {RegistrationId}: level={Level}, row={Row}, col={Col}, {DataSize} bytes (cache={CacheHit})")]
    public static partial void CloudTileServed(ILogger logger, long registrationId, int level, int row, int col, int dataSize, string cacheHit);

    [LoggerMessage(
        EventId = 7907,
        Level = LogLevel.Debug,
        Message = "Cloud COG tile not found for registration {RegistrationId}: level={Level}, row={Row}, col={Col}")]
    public static partial void CloudTileNotFound(ILogger logger, long registrationId, int level, int row, int col);

    [LoggerMessage(
        EventId = 7908,
        Level = LogLevel.Warning,
        Message = "Unsupported compression '{Compression}' in cloud COG {RegistrationId}. JPEG passthrough and DEFLATE decompression are supported.")]
    public static partial void UnsupportedCompression(ILogger logger, string compression, long registrationId);

    [LoggerMessage(
        EventId = 7909,
        Level = LogLevel.Warning,
        Message = "Cloud COG {RegistrationId} uses CRS SRID {Srid} which is not EPSG:3857 or EPSG:4326. Web map clients may display tiles incorrectly.")]
    public static partial void NonWebMercatorCrs(ILogger logger, long registrationId, int srid);
}
