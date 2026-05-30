// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Ai.NlQuery;

/// <summary>
/// Structured logging for NL spatial query operations.
/// Event ID range: 6600-6649.
/// </summary>
internal static partial class NlQueryLog
{
    [LoggerMessage(
        EventId = 6600,
        Level = LogLevel.Information,
        Message = "NL query plan requested for collection {CollectionId} (model={Model})")]
    public static partial void PlanRequested(ILogger logger, string collectionId, string model);

    [LoggerMessage(
        EventId = 6601,
        Level = LogLevel.Information,
        Message = "NL query plan succeeded for collection {CollectionId} (clauses={ClauseCount})")]
    public static partial void PlanSucceeded(ILogger logger, string collectionId, int clauseCount);

    [LoggerMessage(
        EventId = 6602,
        Level = LogLevel.Warning,
        Message = "NL query plan failed for collection {CollectionId}: {Reason}")]
    public static partial void PlanFailed(ILogger logger, string collectionId, string reason);

    [LoggerMessage(
        EventId = 6603,
        Level = LogLevel.Information,
        Message = "NL query plan compiled for collection {CollectionId}")]
    public static partial void CompilationSucceeded(ILogger logger, string collectionId);

    [LoggerMessage(
        EventId = 6604,
        Level = LogLevel.Warning,
        Message = "NL query plan compilation failed for collection {CollectionId}: {Reason}")]
    public static partial void CompilationFailed(ILogger logger, string collectionId, string reason);
}
