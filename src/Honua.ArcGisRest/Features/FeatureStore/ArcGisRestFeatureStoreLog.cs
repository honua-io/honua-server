// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.ArcGisRest.Features.FeatureStore;

/// <summary>
/// Source-generated structured log messages for <see cref="ArcGisRestFeatureStore"/>.
/// </summary>
internal static partial class ArcGisRestFeatureStoreLog
{
    /// <summary>
    /// Debug-level summary of a completed federated query's paging loop (PA-108): how many
    /// upstream <c>/query</c> round-trips were issued and how many features were assembled.
    /// </summary>
    [LoggerMessage(
        EventId = 8100,
        Level = LogLevel.Debug,
        Message = "ArcGIS REST query for layer {ArcGisLayerId} fetched {PageCount} page(s) totaling {FeatureCount} feature(s).")]
    public static partial void QueryPagesFetched(ILogger logger, int arcGisLayerId, int pageCount, int featureCount);
}
