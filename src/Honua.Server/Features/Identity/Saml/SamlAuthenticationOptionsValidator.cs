// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

/// <summary>
/// Fails startup when SAML is enabled without the issuer, audience, and ACS URL the
/// assertion validator requires. A half-configured bridge must not accept assertions.
/// </summary>
internal sealed class SamlAuthenticationOptionsValidator : IValidateOptions<SamlAuthenticationOptions>
{
    /// <inheritdoc />
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
