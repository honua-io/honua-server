// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Licensing;

/// <summary>
/// Registers the Azure Key Vault license-content resolver. Carved out of Honua.Server per the
/// cloud-SDK isolation contract so the Azure.Security.KeyVault.Secrets surface is confined to
/// Honua.Azure; the cloud-neutral licensing pipeline in Honua.Hosting consumes only the
/// <see cref="ILicenseContentSecretResolver"/> abstraction. The resolver is registered as an
/// additive <see cref="ILicenseContentSecretResolver"/> (not via <c>TryAdd</c>) so it coexists with
/// the AWS Secrets Manager resolver in a multi-cloud build: the license service iterates every
/// registered resolver and dispatches by reference prefix. It is cheap and only touches the network
/// when <c>Licensing:LicenseContentSecretRef=azure:keyvault:&lt;vault-uri&gt;/&lt;secret&gt;</c> is set.
/// </summary>
public static class AzureLicenseSecretResolverServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AzureKeyVaultLicenseContentResolver"/> as an
    /// <see cref="ILicenseContentSecretResolver"/> so the license service can load a signed envelope
    /// from Azure Key Vault via
    /// <c>Licensing:LicenseContentSecretRef=azure:keyvault:&lt;vault-uri&gt;/&lt;secret&gt;</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration (reserved for future options).</param>
    /// <returns>The service collection for chaining.</returns>
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
