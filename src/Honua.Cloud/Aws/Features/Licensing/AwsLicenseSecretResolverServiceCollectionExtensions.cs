// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Aws.Features.Licensing;

/// <summary>
/// Registers the AWS Secrets Manager license-content resolver. Carved out of
/// Honua.Server per the cloud-SDK isolation contract so the AWSSDK.SecretsManager
/// surface is confined to Honua.Aws; the cloud-neutral licensing pipeline in
/// Honua.Hosting consumes only the <see cref="ILicenseContentSecretResolver"/>
/// abstraction. The resolver is registered unconditionally (it is cheap and only
/// touches the network when <c>Licensing:LicenseContentSecretRef</c> is set), so a
/// reference can be supplied at any time without changing wiring.
/// </summary>
public static class AwsLicenseSecretResolverServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AwsSecretsManagerLicenseContentResolver"/> as the
    /// <see cref="ILicenseContentSecretResolver"/> so the license service can load a
    /// signed envelope from AWS Secrets Manager via
    /// <c>Licensing:LicenseContentSecretRef=aws:secretsmanager:&lt;arn&gt;</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration (reserved for future options).</param>
    /// <returns>The service collection for chaining.</returns>
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
