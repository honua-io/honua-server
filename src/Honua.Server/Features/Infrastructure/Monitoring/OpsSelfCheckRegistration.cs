// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

internal static class OpsSelfCheckRegistration
{
    public static void AddOpsSelfChecks(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(OpsSelfCheckSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(OpsSelfCheckHttpHandler.Create);
        services.AddSingleton(sp => new OpsSelfCheckEnvironment(
            sp.GetService<OpsKeyRingCertificateLifetime>()?.ExpiresAt,
            OpsSelfCheckEnvironment.ReadVersion()));
        services.AddSingleton<IOpsSelfCheckSource>(sp => new OpsSelfCheckSource(
            sp.GetRequiredService<IHttpClientFactory>(),
            () => ReadProviders(sp),
            sp.GetRequiredService<OpsSelfCheckEnvironment>()));
        services.AddSingleton<OpsSelfChecks>();
        if (ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops(configuration))
        {
            services.AddHostedService<OpsSelfCheckSampler>();
        }
    }

    private static IReadOnlyList<OidcProviderRegistration> ReadProviders(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptionsMonitor<OidcAuthenticationOptions>>().CurrentValue;
        return options.Enabled
            ? OidcProviderCatalog.GetProviders(options, services.GetService<IOidcRuntimeProviderSource>())
            : [];
    }
}
