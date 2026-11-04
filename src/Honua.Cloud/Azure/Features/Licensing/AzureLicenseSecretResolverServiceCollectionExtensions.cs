// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Licensing;

public static class AzureLicenseSecretResolverServiceCollectionExtensions
{
    public static IServiceCollection AddAzureLicenseSecretResolver(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<ILicenseContentSecretResolver, AzureKeyVaultLicenseContentResolver>();
        return services;
    }
}
