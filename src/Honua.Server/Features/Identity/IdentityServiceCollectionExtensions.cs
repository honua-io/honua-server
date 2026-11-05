// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Features.Identity.Saml;
using Honua.Server.Features.Identity.Scim;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.Identity;

public static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddEnterpriseIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ScimProvisioningOptions>()
            .Bind(configuration.GetSection(ScimProvisioningOptions.SectionName))
            .Validate(
                static options => string.IsNullOrWhiteSpace(options.BearerToken)
                    || IsValidOidcIssuer(options.OidcIssuer),
                "Scim:OidcIssuer must be an absolute HTTP(S) URI of at most 2048 characters "
                    + "when Scim:BearerToken enables provisioning.")
            .ValidateOnStart();

        services.AddOptions<SamlAuthenticationOptions>()
            .Bind(configuration.GetSection(SamlAuthenticationOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<SamlAuthenticationOptions>, SamlAuthenticationOptionsValidator>();
        services.TryAddSingleton<ISamlAssertionReplayCache>(static services =>
            new SamlAssertionReplayCache(
                services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                services.GetService<IConnectionMultiplexer>()));
        services.TryAddSingleton<ISamlAssertionValidator, SamlAssertionValidator>();

        return services;
    }

    private static bool IsValidOidcIssuer(string? issuer)
        => !string.IsNullOrWhiteSpace(issuer)
           && issuer.Length <= 2048
           && Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment);
}
