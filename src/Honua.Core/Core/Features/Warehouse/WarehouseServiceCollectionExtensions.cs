// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Warehouse.Abstractions;
using Honua.Core.Features.Warehouse.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Warehouse;

public static class WarehouseServiceCollectionExtensions
{
    public static IServiceCollection AddWarehouseAnalyticsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(WarehouseRuntimeAvailabilityState.Empty);
        services.TryAddSingleton<IWarehouseSourceCapabilityProvider, WarehouseSourceCapabilityProvider>();
        services.TryAddSingleton<IWarehouseQueryPlanExplainer, WarehouseQueryPlanExplainer>();
        return services;
    }
}
