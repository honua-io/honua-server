// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Services;

namespace Honua.Server.Features.Protocols.Coverages.Multidimensional;

internal static class MultidimensionalCoverageServiceCollectionExtensions
{
    public static IServiceCollection AddMultidimensionalCoverageServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IMultidimensionalCoverageMetadataReader, NotEnabledMultidimensionalCoverageMetadataReader>();
        return services;
    }
}
