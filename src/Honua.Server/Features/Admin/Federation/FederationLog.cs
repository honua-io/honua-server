// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Federation;

/// <summary>
/// Source-generated structured logging for the federation admin surface (issue #341).
/// </summary>
internal static partial class FederationLog
{
    [LoggerMessage(EventId = 4500, Level = LogLevel.Information,
        Message = "Listed {Count} federated sources")]
    public static partial void SourcesListed(ILogger logger, int count);

    [LoggerMessage(EventId = 4501, Level = LogLevel.Information,
        Message = "Planned federated query for source '{SourceId}' (localRefinement={RequiresLocalRefinement}, localJoin={RequiresLocalJoin})")]
    public static partial void QueryPlanned(ILogger logger, string sourceId, bool requiresLocalRefinement, bool requiresLocalJoin);
}
