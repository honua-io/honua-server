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
    /// is always bound (unvalidated) and the read-only <see cref="IClientCertificateTrustStore"/>
    /// is always registered so read-only consumers — <c>CapabilityManifestService</c> and the
    /// anonymous admin auth bootstrap endpoint (<c>HandleGetAuthConfig</c>) — can still report
    /// the configured <see cref="ClientCertificateAuthenticationOptions.Mode"/> and trust-profile
    /// hints without eagerly validating or standing up the auth surface. While the capability is
    /// off the enforcement surface stays fully dormant: no eager options validation, no
    /// authentication scheme, no DI-registered validator/extractor — an operator who has
    /// configured (or half-configured) <c>Authentication:ClientCertificates:*</c> without opting
    /// into the experimental flag never hits a startup validation failure or an interposed
    /// cert-RBAC check on an otherwise-valid bearer-token admin request.
    /// </summary>
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
