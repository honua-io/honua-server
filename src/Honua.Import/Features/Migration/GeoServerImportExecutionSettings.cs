// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Migration;

internal static class GeoServerImportExecutionSettings
{
    internal const string AllowUnsafeLocalUrlsInTestEnvironmentKey = "HONUA_TEST_ALLOW_UNSAFE_GEOSERVER_URLS";

    public static bool ShouldAllowUnsafeLocalUrls(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return ShouldAllowUnsafeLocalUrls(
            services.GetRequiredService<IHostEnvironment>(),
            services.GetRequiredService<IConfiguration>());
    }

    public static bool ShouldAllowUnsafeLocalUrls(
        IHostEnvironment hostEnvironment,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(configuration);

        return hostEnvironment.IsEnvironment("Test") &&
               configuration.GetValue<bool>(AllowUnsafeLocalUrlsInTestEnvironmentKey);
    }
}
