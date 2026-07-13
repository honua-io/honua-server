// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Extensions;
using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Honua.Server.Startup;

/// <summary>
/// Wires the file-backed license service plus its three Core-facing facades
/// (<see cref="ILicenseEntitlementService"/>, <see cref="ILicenseStatusProvider"/>,
/// <see cref="ILicenseManager"/>) and registers the resilient identity-provider HTTP clients
/// used by the admin auth flows.
/// </summary>
internal static class LicensingRegistration
{
    public static IServiceCollection AddHonuaLicensing(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<LicenseOptions>(
            configuration.GetSection(LicenseOptions.SectionName));
        services.Configure<LicenseCapacityOptions>(
            configuration.GetSection(LicenseCapacityOptions.SectionName));
        services.AddSingleton<IEd25519Verifier, BouncyCastleEd25519Verifier>();

        // Register the provider-specific license-content secret resolvers so the license
        // service can load a signed envelope from a secret store (AWS Secrets Manager via
        // aws:secretsmanager:, Azure Key Vault via azure:keyvault:) using
        // Licensing:LicenseContentSecretRef. The AWSSDK / Azure SDK surfaces stay confined to
        // Honua.Aws / Honua.Azure (cloud-SDK isolation contract); the cloud-neutral pipeline
        // consumes only the ILicenseContentSecretResolver abstraction, iterates every registered
        // resolver, dispatches by reference prefix, and falls back to Community when none matches.
#if !HONUA_EXCLUDE_AWS
        Honua.Aws.Features.Licensing.AwsLicenseSecretResolverServiceCollectionExtensions
            .AddAwsLicenseSecretResolver(services, configuration);
#endif
#if !HONUA_EXCLUDE_AZURE
        Honua.Licensing.AzureLicenseSecretResolverServiceCollectionExtensions
            .AddAzureLicenseSecretResolver(services, configuration);
#endif

        services.AddSingleton<FileBackedLicenseService>();
        services.AddSingleton<ILicenseEntitlementService>(sp =>
            sp.GetRequiredService<FileBackedLicenseService>());
        services.AddSingleton<ILicenseStatusProvider>(sp =>
            sp.GetRequiredService<FileBackedLicenseService>());
        services.AddSingleton<ILicenseManager>(sp =>
            sp.GetRequiredService<FileBackedLicenseService>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<FileBackedLicenseService>());

        services.AddSingleton<LicenseCapacityMeter>(sp =>
            new LicenseCapacityMeter(
                sp.GetRequiredService<ILicenseEntitlementService>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LicenseCapacityOptions>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<LicenseCapacityMeter>>(),
                sp.GetService<IConnectionMultiplexer>()));
        services.AddSingleton<ILicenseCapacityMeter>(sp =>
            sp.GetRequiredService<LicenseCapacityMeter>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<LicenseCapacityMeter>());

        // Feed the migration node-version barrier (#2812) from the meter's live, coordinated node
        // inventory. When Redis is absent the inventory reports no coordination and the barrier stays
        // inert, so single-node/dev-compose boot is unaffected and needs zero new configuration.
        services.AddSingleton<Honua.Core.Features.Infrastructure.Migrations.IActiveNodeVersionInventory>(sp =>
            new LicenseCapacityNodeVersionInventory(sp.GetRequiredService<LicenseCapacityMeter>()));

        // Test/dev only: an explicit Licensing:DevGrantEdition grants every entitlement up to that
        // edition without a signed license, so an out-of-process test/CI server can exercise
        // edition-gated features (e.g. FeatureServer editing, honua-server#1591). It is registered
        // last so it wins ILicenseEntitlementService resolution. It is FAIL-CLOSED in Production:
        // setting it there throws at startup rather than silently bypassing every edition gate.
        var devGrantEdition = configuration[$"{LicenseOptions.SectionName}:DevGrantEdition"];
        if (!string.IsNullOrWhiteSpace(devGrantEdition))
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    $"{LicenseOptions.SectionName}:DevGrantEdition is a test/dev-only license override and must not be " +
                    "set in the Production environment; it would bypass every edition gate without a signed license. " +
                    "Remove it or install a signed license.");
            }

            if (Enum.TryParse<HonuaEdition>(devGrantEdition, ignoreCase: true, out var grantEdition))
            {
                services.AddSingleton<ILicenseEntitlementService>(sp =>
                    new DevLicenseEntitlementService(
                        grantEdition,
                        sp.GetService<ILogger<DevLicenseEntitlementService>>()));
            }
        }

        // Named HTTP clients for identity provider connectivity tests with resilience.
        services.AddResilientHttpClient(
            "IdentityProviderTest",
            "identity-provider-test",
            HttpResiliencePolicies.FastApiDefaults);
        services.AddResilientHttpClient(
            "AdminAuthOidc",
            "admin-auth-oidc",
            HttpResiliencePolicies.FastApiDefaults,
            configureHandler: () => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });

        return services;
    }
}
