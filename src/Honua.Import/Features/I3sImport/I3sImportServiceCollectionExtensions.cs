// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Service-collection extensions for the I3S/.slpk admin import surface.
/// </summary>
internal static class I3sImportServiceCollectionExtensions
{
    /// <summary>
    /// Registers the I3S/.slpk import service and its bound options.
    /// </summary>
    public static IServiceCollection AddI3sSlpkImport(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<I3sImportOptions>()
            .Bind(configuration.GetSection(I3sImportOptions.SectionName));

        services.AddScoped<I3sSlpkImportService>();
        return services;
    }
}
