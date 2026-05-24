// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal sealed class ClientCertificateAuthenticationOptionsValidator
    : IValidateOptions<ClientCertificateAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, ClientCertificateAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.Mode != ClientCertificateAuthenticationMode.Disabled &&
            string.IsNullOrWhiteSpace(options.EnvironmentId))
        {
            failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:EnvironmentId is required when client-certificate authentication is enabled.");
        }

        ValidateForwardedCertificate(options, failures);
        ValidateProfiles(options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateForwardedCertificate(
        ClientCertificateAuthenticationOptions options,
        List<string> failures)
    {
        if (!options.ForwardedCertificate.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ForwardedCertificate.HeaderName))
        {
            failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:ForwardedCertificate:HeaderName is required when forwarded certificates are enabled.");
        }

        if (options.ForwardedCertificate.TrustedProxyNetworks.Length == 0)
        {
            failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:ForwardedCertificate:TrustedProxyNetworks must include at least one exact IP or CIDR network.");
        }
    }

    private static void ValidateProfiles(ClientCertificateAuthenticationOptions options, List<string> failures)
    {
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in options.TrustProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.ProfileId))
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles entries require ProfileId.");
                continue;
            }

            if (!profileIds.Add(profile.ProfileId))
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles contains duplicate ProfileId '{profile.ProfileId}'.");
            }

            if (string.IsNullOrWhiteSpace(profile.EnvironmentId))
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles:{profile.ProfileId}:EnvironmentId is required.");
            }

            if (profile.ExpirationWarningThresholdDays < 0)
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles:{profile.ProfileId}:ExpirationWarningThresholdDays cannot be negative.");
            }

            if (profile.RotationGracePeriodDays < 0)
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles:{profile.ProfileId}:RotationGracePeriodDays cannot be negative.");
            }

            if (profile.Enabled &&
                !profile.AcceptedIssuerSubjects.Any(static value => !string.IsNullOrWhiteSpace(value)) &&
                !profile.AcceptedIssuerThumbprints.Any(static value => !string.IsNullOrWhiteSpace(value)) &&
                !profile.CustomTrustAnchorCertificates.Any(static value => !string.IsNullOrWhiteSpace(value)))
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles:{profile.ProfileId} must include at least one accepted issuer subject, accepted issuer thumbprint, or custom trust anchor certificate.");
            }

            ValidateCustomTrustAnchors(profile, failures);
            ValidateMappings(profile, failures);
        }
    }

    private static void ValidateCustomTrustAnchors(
        ClientCertificateTrustProfileOptions profile,
        List<string> failures)
    {
        for (var index = 0; index < profile.CustomTrustAnchorCertificates.Length; index++)
        {
            var anchor = profile.CustomTrustAnchorCertificates[index];
            if (string.IsNullOrWhiteSpace(anchor))
            {
                continue;
            }

            if (!ClientCertificateValidator.TryParseTrustAnchor(anchor, out var parsed))
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles:{profile.ProfileId}:CustomTrustAnchorCertificates[{index}] is not a valid PEM or base64 DER certificate.");
                continue;
            }

            parsed.Dispose();
        }
    }

    private static void ValidateMappings(
        ClientCertificateTrustProfileOptions profile,
        List<string> failures)
    {
        var mappingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in profile.PrincipalMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.MappingId))
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles:{profile.ProfileId}:PrincipalMappings entries require MappingId.");
            }
            else if (!mappingIds.Add(mapping.MappingId))
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles:{profile.ProfileId}:PrincipalMappings contains duplicate MappingId '{mapping.MappingId}'.");
            }

            if (string.IsNullOrWhiteSpace(mapping.MatchValue))
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles:{profile.ProfileId}:PrincipalMappings:{mapping.MappingId}:MatchValue is required.");
            }

            if (string.IsNullOrWhiteSpace(mapping.PrincipalId))
            {
                failures.Add($"{ClientCertificateAuthenticationOptions.SectionName}:TrustProfiles:{profile.ProfileId}:PrincipalMappings:{mapping.MappingId}:PrincipalId is required.");
            }
        }
    }
}
