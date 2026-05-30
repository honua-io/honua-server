// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.GeoServices.GeometryService;

/// <summary>
/// Structured logging for GeometryService endpoints with source generation (AOT compatible).
/// EventId range: 5500-5529
/// </summary>
internal static partial class GeometryServiceLog
{
    /// <summary>
    /// Logs when a buffer operation completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 5500,
        Level = LogLevel.Information,
        Message = "Buffer operation completed: {GeometryCount} geometries, distance={Distance}, geodesic={Geodesic}")]
    public static partial void BufferOperationCompleted(ILogger logger, int geometryCount, double distance, bool geodesic);

    /// <summary>
    /// Logs when a simplify operation completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 5501,
        Level = LogLevel.Information,
        Message = "Simplify operation completed: {GeometryCount} geometries (topological correction)")]
    public static partial void SimplifyOperationCompleted(ILogger logger, int geometryCount);

    /// <summary>
    /// Logs when a project operation completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 5502,
        Level = LogLevel.Information,
        Message = "Project operation completed: {GeometryCount} geometries, {FromSrid} -> {ToSrid}")]
    public static partial void ProjectOperationCompleted(ILogger logger, int geometryCount, int fromSrid, int toSrid);

    /// <summary>
    /// Logs when a geometry operation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 5503,
        Level = LogLevel.Error,
        Message = "Geometry operation '{Operation}' failed: {ErrorMessage}")]
    public static partial void GeometryOperationFailed(ILogger logger, string operation, string errorMessage, Exception? exception = null);

    /// <summary>
    /// Logs when invalid geometry input is received.
    /// </summary>
    [LoggerMessage(
        EventId = 5504,
        Level = LogLevel.Warning,
        Message = "Invalid geometry input for '{Operation}': {Reason}")]
    public static partial void InvalidGeometryInput(ILogger logger, string operation, string reason);

    /// <summary>
    /// Logs when an unsupported CRS is referenced.
    /// </summary>
    [LoggerMessage(
        EventId = 5505,
        Level = LogLevel.Warning,
        Message = "Unsupported CRS referenced: SRID {Srid}")]
    public static partial void UnsupportedCrs(ILogger logger, int srid);

    [LoggerMessage(
        EventId = 5506,
        Level = LogLevel.Debug,
        Message = "Geometry service request parsed: operation={Operation}, geometryCount={GeometryCount}, geometryType={GeometryType}")]
    public static partial void RequestParsed(ILogger logger, string operation, int geometryCount, string? geometryType);

    [LoggerMessage(
        EventId = 5507,
        Level = LogLevel.Information,
        Message = "Union operation completed: {GeometryCount} geometries")]
    public static partial void UnionOperationCompleted(ILogger logger, int geometryCount);

    [LoggerMessage(
        EventId = 5508,
        Level = LogLevel.Information,
        Message = "{Operation} operation completed: {GeometryCount} geometries")]
    public static partial void BinaryOperationCompleted(ILogger logger, string operation, int geometryCount);

    [LoggerMessage(
        EventId = 5509,
        Level = LogLevel.Information,
        Message = "{Operation} operation completed: {GeometryCount} geometries measured")]
    public static partial void MeasurementOperationCompleted(ILogger logger, string operation, int geometryCount);
}
