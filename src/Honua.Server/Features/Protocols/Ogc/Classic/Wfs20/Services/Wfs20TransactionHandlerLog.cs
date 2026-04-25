// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

internal static partial class Wfs20TransactionHandlerLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "WFS 2.0 transaction {TransactionId} failed")]
    public static partial void TransactionFailed(ILogger logger, string transactionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute insert operation")]
    public static partial void InsertOperationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute update operation")]
    public static partial void UpdateOperationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute delete operation")]
    public static partial void DeleteOperationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to resolve layer ID for feature type '{FeatureTypeName}'")]
    public static partial void ResolveLayerIdFailed(ILogger logger, string featureTypeName, Exception exception);
}
