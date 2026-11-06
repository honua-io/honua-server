// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Cloud.Aws.Features.Licensing;

public static class AwsLicenseSecretResolverServiceCollectionExtensions
{
    public static IServiceCollection AddAwsLicenseSecretResolver(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Additive (not TryAdd) so it coexists with the Azure Key Vault resolver in a multi-cloud
        // build: the license service iterates every registered ILicenseContentSecretResolver and
        // dispatches by reference prefix (aws:secretsmanager: here).
        services.AddSingleton<ILicenseContentSecretResolver, AwsSecretsManagerLicenseContentResolver>();
        return services;
    }
}
