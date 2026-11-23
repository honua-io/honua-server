// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Db.DuckDB.Features.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Honua.Db.DuckDB.Features.FeatureStore.Services;

internal sealed class DuckDBManagedDataAccessFactory(
    DuckDBManagedSource source,
    ILogger<DuckDBFeatureDataAccess> logger,
    IPerformanceMonitor? performanceMonitor = null)
{
    internal DuckDBFeatureDataAccess Create(DuckDBLayerMapping mapping, DuckDBLayerRegistry registry)
        => new(source.CreateConnectionProvider(mapping), registry, performanceMonitor, logger);
}
