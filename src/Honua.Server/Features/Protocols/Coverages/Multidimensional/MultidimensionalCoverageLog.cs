// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.Coverages.Multidimensional;

/// <summary>
/// Structured logging for cloud-optimized HDF5 / NetCDF4 coverage operations.
/// Event ID range: 8000-8049.
/// </summary>
internal static partial class MultidimensionalCoverageLog
{
    [LoggerMessage(
        EventId = 8000,
        Level = LogLevel.Information,
        Message = "Registered multidim coverage '{Name}' (ID {RegistrationId}) for layer {LayerId}: {Format} via {Provider}://{Bucket}/{ObjectKey}")]
    public static partial void Registered(
        ILogger logger,
        string name,
        long registrationId,
        int layerId,
        MultidimensionalCoverageFormat format,
        CloudStorageProvider provider,
        string bucket,
        string objectKey);

    [LoggerMessage(
        EventId = 8001,
        Level = LogLevel.Information,
        Message = "Unregistered multidim coverage {RegistrationId}")]
    public static partial void Unregistered(ILogger logger, long registrationId);

    [LoggerMessage(
        EventId = 8002,
        Level = LogLevel.Information,
        Message = "Listed {Count} multidim coverage registrations for layer {LayerId}")]
    public static partial void Listed(ILogger logger, int count, int layerId);

    [LoggerMessage(
        EventId = 8003,
        Level = LogLevel.Warning,
        Message = "Multidim coverage {RegistrationId} metadata scan rejected: reader not enabled. See ADR-0039.")]
    public static partial void MetadataReaderUnavailable(ILogger logger, long registrationId);

    [LoggerMessage(
        EventId = 8004,
        Level = LogLevel.Warning,
        Message = "Multidim coverage {RegistrationId} metadata scan rejected: unsupported layout. {Reason}")]
    public static partial void MetadataUnsupportedLayout(ILogger logger, long registrationId, string reason);

    [LoggerMessage(
        EventId = 8005,
        Level = LogLevel.Information,
        Message = "Multidim coverage {RegistrationId} metadata scan completed: {VariableCount} variables, SRID {Srid}")]
    public static partial void MetadataScanCompleted(ILogger logger, long registrationId, int variableCount, int srid);
}
