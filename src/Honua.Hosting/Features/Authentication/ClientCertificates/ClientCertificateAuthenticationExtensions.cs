// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal static class ClientCertificateAuthenticationExtensions
{
    /// <summary>
    /// Registers native mTLS/client-certificate authentication ONLY when the
    /// <c>security.mtls</c> experimental capability is enabled (#2958). The options section
    /// is always bound (unvalidated) so read-only consumers such as
    /// <c>CapabilityManifestService</c> can still report the configured
    /// <see cref="ClientCertificateAuthenticationOptions.Mode"/> without eagerly validating
    /// or standing up the auth surface. While the capability is off the feature stays fully
    /// dormant: no eager options validation, no authentication scheme, no DI-registered
    /// validator/trust-store — an operator who has configured (or half-configured)
    /// <c>Authentication:ClientCertificates:*</c> without opting into the experimental flag
    /// never hits a startup validation failure or an interposed cert-RBAC check on an
    /// otherwise-valid bearer-token admin request.
    /// </summary>
    public static IServiceCollection AddHonuaClientCertificateAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var optionsBuilder = services.AddOptions<ClientCertificateAuthenticationOptions>()
            .Bind(configuration.GetSection(ClientCertificateAuthenticationOptions.SectionName));

        if (!IsMtlsCapabilityEnabled(configuration))
        {
            return services;
        }

        optionsBuilder.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ClientCertificateAuthenticationOptions>,
            ClientCertificateAuthenticationOptionsValidator>());
        services.TryAddSingleton<ClientCertificateExtractor>();
        services.TryAddSingleton<IClientCertificateTrustStore, InMemoryClientCertificateTrustStore>();
        services.TryAddSingleton<IClientCertificateValidator, ClientCertificateValidator>();
        services.TryAddSingleton<ClientCertificateAuthenticationDependencies>();

        _ = services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ClientCertificateAuthenticationHandler>(
                ClientCertificateAuthenticationDefaults.AuthenticationScheme,
                static _ => { });

        return services;
    }

    /// <summary>
    /// Runs the client-certificate enforcement middleware ONLY when the <c>security.mtls</c>
    /// experimental capability is enabled (#2958); otherwise it is a no-op so the pipeline
    /// never attempts to resolve the (unregistered, when the flag is off) enforcement
    /// dependencies.
    /// </summary>
    public static IApplicationBuilder UseHonuaClientCertificateAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        return IsMtlsCapabilityEnabled(configuration)
            ? app.UseMiddleware<ClientCertificateEnforcementMiddleware>()
            : app;
    }

    /// <summary>
    /// Whether the <c>security.mtls</c> built-experimental capability is enabled, per the
    /// same <c>Capabilities:Experimental</c> config precedence as every other experimental
    /// capability (global switch or per-capability override; see
    /// <see cref="CapabilityFlagOptions"/>). This is a one-time, startup-time (or per-request,
    /// for the composite-scheme selector) config read rather than an injected
    /// <c>IOptions&lt;CapabilityFlagOptions&gt;</c> because several call sites run at
    /// composition time, before the DI container is built.
    /// </summary>
    public static bool IsMtlsCapabilityEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var flags = new CapabilityFlagOptions();
        CapabilityFlagOptions.Bind(flags, configuration.GetSection(CapabilityFlagOptions.SectionName));
        return flags.IsExperimentalEnabled(ClientCertificateAuthenticationDefaults.CapabilityId);
    }
}
