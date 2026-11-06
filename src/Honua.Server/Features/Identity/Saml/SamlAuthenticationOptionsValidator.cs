// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

// Startup fails when SAML is enabled without the issuer, audience, and consumer URL, so a half-configured bridge cannot accept assertions.
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

        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "SAML is enabled but missing: " + string.Join(", ", missing) + ".");
    }
}
