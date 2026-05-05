// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.MySql;

internal static partial class MySqlLog
{
    [LoggerMessage(EventId = 8900, Level = LogLevel.Information, Message = "MySQL/MariaDB layer catalog initialized with {LayerCount} layers and {ServiceCount} services")]
    public static partial void LayerCatalogInitialized(ILogger logger, int layerCount, int serviceCount);

    [LoggerMessage(EventId = 8901, Level = LogLevel.Information, Message = "Registered MySQL/MariaDB layer {LayerId}: table={Table}, schema={Schema}, geom={GeomCol}, pk={PkCol}, attrs={AttrCount}")]
    public static partial void LayerRegistered(ILogger logger, int layerId, string table, string? schema, string geomCol, string pkCol, int attrCount);

    [LoggerMessage(EventId = 8902, Level = LogLevel.Error, Message = "MySQL/MariaDB {OperationType} query failed for layer {LayerId}.")]
    public static partial void QueryFailed(ILogger logger, string operationType, int layerId, Exception exception);
}
