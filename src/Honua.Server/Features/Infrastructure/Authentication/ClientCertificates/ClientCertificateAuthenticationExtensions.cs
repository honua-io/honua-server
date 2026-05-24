// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal static class ClientCertificateAuthenticationExtensions
{
    public static IServiceCollection AddHonuaClientCertificateAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ClientCertificateAuthenticationOptions>()
            .Bind(configuration.GetSection(ClientCertificateAuthenticationOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ClientCertificateAuthenticationOptions>,
            ClientCertificateAuthenticationOptionsValidator>());
        services.TryAddSingleton<ClientCertificateExtractor>();
        services.TryAddSingleton<IClientCertificateTrustStore, InMemoryClientCertificateTrustStore>();
        services.TryAddSingleton<IClientCertificateValidator, ClientCertificateValidator>();

        _ = services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ClientCertificateAuthenticationHandler>(
                ClientCertificateAuthenticationDefaults.AuthenticationScheme,
                static _ => { });

        return services;
    }

    public static IApplicationBuilder UseHonuaClientCertificateAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ClientCertificateEnforcementMiddleware>();
    }
}
