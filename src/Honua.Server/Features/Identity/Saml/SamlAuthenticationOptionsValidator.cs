// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

// Startup fails when SAML is enabled without the issuer, audience, and consumer URL, so a half-configured bridge cannot accept assertions.
// A configured IdP sign-on URL must be absolute https, because the browser is redirected there with the sign-in request.
internal sealed class SamlAuthenticationOptionsValidator : IValidateOptions<SamlAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, SamlAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.EntityId))
        {
            missing.Add(nameof(options.EntityId));
        }

        if (string.IsNullOrWhiteSpace(options.IdpEntityId))
        {
            missing.Add(nameof(options.IdpEntityId));
        }

        if (string.IsNullOrWhiteSpace(options.AssertionConsumerServiceUrl))
        {
            missing.Add(nameof(options.AssertionConsumerServiceUrl));
        }

        if (missing.Count > 0)
        {
            return ValidateOptionsResult.Fail("SAML is enabled but missing: " + string.Join(", ", missing) + ".");
        }

        return IsAbsoluteHttpsUrlOrUnset(options.IdpSingleSignOnServiceUrl)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(nameof(options.IdpSingleSignOnServiceUrl) + " must be an absolute https URL.");
    }

    private static bool IsAbsoluteHttpsUrlOrUnset(string? value)
        => string.IsNullOrWhiteSpace(value)
            || (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);
}
