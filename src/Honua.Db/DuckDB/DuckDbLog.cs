// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.DuckDB;

internal static partial class DuckDbLog
{
    [LoggerMessage(EventId = 8800, Level = LogLevel.Information, Message = "DuckDB layer catalog initialized with {LayerCount} layers and {ServiceCount} services")]
    public static partial void LayerCatalogInitialized(ILogger logger, int layerCount, int serviceCount);

    [LoggerMessage(EventId = 8801, Level = LogLevel.Information, Message = "Installing DuckDB {Extension} extension from offline path: {Path}")]
    public static partial void InstallingDuckDbExtensionFromOfflinePath(ILogger logger, string extension, string path);

    [LoggerMessage(EventId = 8802, Level = LogLevel.Information, Message = "DuckDB {Extension} extension installed")]
    public static partial void DuckDbExtensionInstalled(ILogger logger, string extension);

    [LoggerMessage(EventId = 8803, Level = LogLevel.Information, Message = "Registered DuckDB layer {LayerId}: table={Table}, geom={GeomCol}, oid={OidCol}, attrs={AttrCount}")]
    public static partial void LayerRegistered(ILogger logger, int layerId, string table, string geomCol, string oidCol, int attrCount);

    [LoggerMessage(
        EventId = 8804,
        Level = LogLevel.Warning,
        Message = "Could not discover attribute columns for DuckDB layer {LayerId} table {Table}; set DuckDB:Layers:N:Attributes in configuration to specify columns explicitly")]
    public static partial void AttributeDiscoveryFailed(ILogger logger, int layerId, string table, Exception exception);

    [LoggerMessage(EventId = 8805, Level = LogLevel.Error, Message = "DuckDB {OperationType} query failed for layer {LayerId}.")]
    public static partial void QueryFailed(ILogger logger, string operationType, int layerId, Exception exception);
}
