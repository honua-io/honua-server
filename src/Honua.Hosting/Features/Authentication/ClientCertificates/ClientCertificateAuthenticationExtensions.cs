// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal static class ClientCertificateAuthenticationExtensions
{
    // While security.mtls is off, options stay readable for capability reporting but no certificate scheme or validator is registered, so a bearer admin request is not certificate-checked.
    public static IServiceCollection AddHonuaClientCertificateAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var optionsBuilder = services.AddOptions<ClientCertificateAuthenticationOptions>()
            .Bind(configuration.GetSection(ClientCertificateAuthenticationOptions.SectionName));

        // Registered unconditionally (unlike the scheme/validator/enforcement below): the
        // anonymous admin auth bootstrap endpoint (HandleGetAuthConfig) resolves this via
        // [FromServices] to report trust-profile hints regardless of whether the experimental
        // capability is opted in, so it must stay resolvable even while mTLS is gated off.
        // The in-memory store only seeds itself from the (unvalidated) bound options and has
        // no other side effects, so registering it does not stand up any auth surface.
        services.TryAddSingleton<IClientCertificateTrustStore, InMemoryClientCertificateTrustStore>();

        if (!IsMtlsCapabilityEnabled(configuration))
        {
            return services;
        }

        optionsBuilder.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ClientCertificateAuthenticationOptions>,
            ClientCertificateAuthenticationOptionsValidator>());
        services.TryAddSingleton<ClientCertificateExtractor>();
        services.TryAddSingleton<IClientCertificateValidator, ClientCertificateValidator>();
        services.TryAddSingleton<ClientCertificateAuthenticationDependencies>();

        _ = services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ClientCertificateAuthenticationHandler>(
                ClientCertificateAuthenticationDefaults.AuthenticationScheme,
                static _ => { });

        return services;
    }

    // Enforcement is a no-op while the capability is off so the pipeline does not resolve dependencies that were not registered.
    public static IApplicationBuilder UseHonuaClientCertificateAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        return IsMtlsCapabilityEnabled(configuration)
            ? app.UseMiddleware<ClientCertificateEnforcementMiddleware>()
            : app;
    }

    // The capability flag is read from configuration because these call sites run at composition time, before DI is built.
    public static bool IsMtlsCapabilityEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var flags = new CapabilityFlagOptions();
        CapabilityFlagOptions.Bind(flags, configuration.GetSection(CapabilityFlagOptions.SectionName));
        return flags.IsExperimentalEnabled(ClientCertificateAuthenticationDefaults.CapabilityId);
    }
}
