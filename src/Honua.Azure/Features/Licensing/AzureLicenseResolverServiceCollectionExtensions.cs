// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Licensing;

/// <summary>
/// Registers the Azure Key Vault license-content resolver
/// (<see cref="AzureKeyVaultLicenseContentResolver"/>) so <c>FileBackedLicenseService</c> can resolve
/// a <c>Licensing:LicenseContentSecretRef = azure:keyvault:...</c> envelope at startup. Invoked by
/// the composition root only when the Azure module is compiled in.
/// </summary>
/// <remarks>
/// PROVISIONAL draft (#1745) pending the canonical resolver seam in honua-server#1742. Registers a
/// short-timeout named HttpClient for the Key Vault call and a separate one for the IMDS
/// managed-identity token endpoint. Kept SEPARATE from the database connection-secret resolver.
/// </remarks>
internal static class AzureLicenseResolverServiceCollectionExtensions
{
    public static IServiceCollection AddAzureKeyVaultLicenseContentResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient("AzureKeyVaultLicense", client =>
            client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient("AzureManagedIdentityLicense", client =>
            client.Timeout = TimeSpan.FromSeconds(5));

        services.TryAddSingleton<ILicenseContentSecretResolver, AzureKeyVaultLicenseContentResolver>();
        return services;
    }
}
