// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Source-generated logging for the Honua Operations Toolset.
/// </summary>
internal static partial class OperationsLog
{
    [LoggerMessage(119000, LogLevel.Information, "Operation catalog listed {OperationCount} operations with version {CatalogVersion}")]
    public static partial void OperationCatalogListed(ILogger logger, int operationCount, string catalogVersion);

    [LoggerMessage(119001, LogLevel.Warning, "Operation provider {ProviderId} failed")]
    public static partial void OperationProviderFailed(ILogger logger, string providerId, Exception exception);

    [LoggerMessage(119002, LogLevel.Information, "Operation {OperationId} policy decision resolved to {Outcome}")]
    public static partial void OperationPolicyDecision(ILogger logger, string operationId, OperationPolicyOutcome outcome);

    [LoggerMessage(119003, LogLevel.Information, "Operation {OperationId} executed successfully")]
    public static partial void OperationExecuted(ILogger logger, string operationId);

    [LoggerMessage(119004, LogLevel.Warning, "Operation {OperationId} was not found in the catalog")]
    public static partial void OperationNotFound(ILogger logger, string operationId);
}
