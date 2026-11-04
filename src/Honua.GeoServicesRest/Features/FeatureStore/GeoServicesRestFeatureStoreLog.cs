// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.GeoServicesRest.Features.FeatureStore;

internal static partial class GeoServicesRestFeatureStoreLog
{
    [LoggerMessage(
        EventId = 8100,
        Level = LogLevel.Debug,
        Message = "ArcGIS REST query for layer {ArcGisLayerId} fetched {PageCount} page(s) totaling {FeatureCount} feature(s).")]
    public static partial void QueryPagesFetched(ILogger logger, int arcGisLayerId, int pageCount, int featureCount);
}
