// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Extensions;
using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Startup;

/// <summary>
/// Wires the file-backed license service plus its three Core-facing facades
/// (<see cref="ILicenseEntitlementService"/>, <see cref="ILicenseStatusProvider"/>,
/// <see cref="ILicenseManager"/>) and registers the resilient identity-provider HTTP clients
/// used by the admin auth flows.
/// </summary>
internal static class LicensingRegistration
{
    public static IServiceCollection AddHonuaLicensing(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LicenseOptions>(
            configuration.GetSection(LicenseOptions.SectionName));
        services.AddSingleton<IEd25519Verifier, BouncyCastleEd25519Verifier>();
        services.AddSingleton<FileBackedLicenseService>();
        services.AddSingleton<ILicenseEntitlementService>(sp =>
            sp.GetRequiredService<FileBackedLicenseService>());
        services.AddSingleton<ILicenseStatusProvider>(sp =>
            sp.GetRequiredService<FileBackedLicenseService>());
        services.AddSingleton<ILicenseManager>(sp =>
            sp.GetRequiredService<FileBackedLicenseService>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<FileBackedLicenseService>());

        // Test/dev only: an explicit Licensing:DevGrantEdition grants every entitlement up to that
        // edition without a signed license, so an out-of-process test/CI server can exercise
        // edition-gated features (e.g. feature editing, honua-server#1548/#1577). Default off; the
        // override is registered last so it wins ILicenseEntitlementService resolution. Never set
        // this in production.
        var devGrantEdition = configuration[$"{LicenseOptions.SectionName}:DevGrantEdition"];
        if (!string.IsNullOrWhiteSpace(devGrantEdition)
            && Enum.TryParse<HonuaEdition>(devGrantEdition, ignoreCase: true, out var grantEdition))
        {
            services.AddSingleton<ILicenseEntitlementService>(sp =>
                new DevLicenseEntitlementService(
                    grantEdition,
                    sp.GetService<ILogger<DevLicenseEntitlementService>>()));
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
