// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Warehouse.Abstractions;
using Honua.Core.Features.Warehouse.Domain;
using Honua.Core.Features.Warehouse.Services;

namespace Honua.Db.DuckDB.Features.Infrastructure;

internal sealed class DuckDbWarehouseCapabilityDescriptor : IWarehouseProviderCapabilityDescriptor
{
    public string ProviderName => DataProviderNames.DuckDb;

    public WarehouseEffectiveSourceCapabilities Describe(WarehouseSourceCapabilityRequest request)
        => WarehouseSourceCapabilityProvider.BuildBaselineCapabilities(DataProviderNames.DuckDb, request);
}
