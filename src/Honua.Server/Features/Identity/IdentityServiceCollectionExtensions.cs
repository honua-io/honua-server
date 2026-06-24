// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Identity.Saml;
using Honua.Server.Features.Identity.Scim;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Identity;

/// <summary>
/// Registration for the enterprise identity slice: SCIM 2.0 provisioning (#510) and the
/// SAML 2.0 Service Provider SSO bridge (#508). Both adapt into the existing identity/role
/// model rather than introducing a parallel one — SCIM provisions users and maps groups to
/// roles over the shared user store, and SAML validates an assertion and establishes the same
/// session/claims model as OIDC.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers SCIM and SAML options and the SAML assertion validator. The SCIM/SAML stores
    /// themselves are projected onto the shared in-memory IAM defaults
    /// (see <c>ControlPlaneIamDefaults</c>).
    /// </summary>
    public static IServiceCollection AddEnterpriseIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ScimProvisioningOptions>()
            .Bind(configuration.GetSection(ScimProvisioningOptions.SectionName));

        services.AddOptions<SamlAuthenticationOptions>()
            .Bind(configuration.GetSection(SamlAuthenticationOptions.SectionName));

        services.TryAddSingleton<ISamlAssertionValidator, SamlAssertionValidator>();

        return services;
    }
}
