// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

// An enabled SAML IdP is a trusted provider next to the OIDC ones; its sessions carry auth_type "saml".
internal sealed class SamlIdentityProviderSource(IOptions<SamlAuthenticationOptions> options) : IExternalIdentityProviderSource
{
    public IEnumerable<ExternalIdentityProviderRegistration> GetProviders()
    {
        var saml = options.Value;
        if (!saml.Enabled)
        {
            return [];
        }

        var entityId = string.IsNullOrWhiteSpace(saml.IdpEntityId) ? "idp" : saml.IdpEntityId.Trim();
        return [new ExternalIdentityProviderRegistration($"saml:{entityId}", "saml")];
    }
}
