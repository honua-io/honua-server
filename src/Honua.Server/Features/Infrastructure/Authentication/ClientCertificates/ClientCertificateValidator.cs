// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Formats.Asn1;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal sealed class ClientCertificateValidator(
    IOptionsMonitor<ClientCertificateAuthenticationOptions> options,
    IClientCertificateTrustStore trustStore,
    TimeProvider? timeProvider = null) : IClientCertificateValidator
{
    private const string ClientAuthenticationEku = "1.3.6.1.5.5.7.3.2";
    private readonly IOptionsMonitor<ClientCertificateAuthenticationOptions> _options = options;
    private readonly IClientCertificateTrustStore _trustStore = trustStore;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ClientCertificateValidationResult> ValidateAsync(
        X509Certificate2? certificate,
        CancellationToken cancellationToken = default)
    {
        if (certificate is null)
        {
            return ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.MissingCertificate,
                "A client certificate is required for this request.");
        }

        var now = _timeProvider.GetUtcNow();
        var fingerprint = ComputeSha256Fingerprint(certificate);
        var issuerHash = ComputeIssuerHash(certificate.Issuer);
        var daysUntilExpiry = Math.Max(0, (int)Math.Floor((certificate.NotAfter.ToUniversalTime() - now.UtcDateTime).TotalDays));

        if (now.UtcDateTime < certificate.NotBefore.ToUniversalTime())
        {
            return ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.CertificateNotYetValid,
                "The client certificate is not valid yet.",
                fingerprintSha256: fingerprint,
                issuerHash: issuerHash,
                daysUntilExpiry: daysUntilExpiry);
        }

        if (now.UtcDateTime > certificate.NotAfter.ToUniversalTime())
        {
            return ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.CertificateExpired,
                "The client certificate has expired.",
                fingerprintSha256: fingerprint,
                issuerHash: issuerHash,
                daysUntilExpiry: 0);
        }

        var environmentId = _options.CurrentValue.EnvironmentId;
        var profiles = await _trustStore.ListProfilesAsync(environmentId, cancellationToken).ConfigureAwait(false);
        var enabledProfiles = profiles
            .Where(static profile => profile.Enabled)
            .ToArray();
        if (enabledProfiles.Length == 0)
        {
            var issuerMatchesAnotherEnvironment = await IssuerMatchesAnotherEnvironmentAsync(
                certificate,
                environmentId,
                cancellationToken).ConfigureAwait(false);
            var code = issuerMatchesAnotherEnvironment
                ? ClientCertificateValidationErrorCode.WrongEnvironment
                : ClientCertificateValidationErrorCode.UntrustedIssuer;
            return ClientCertificateValidationResult.Failure(
                code,
                code == ClientCertificateValidationErrorCode.WrongEnvironment
                    ? "The client certificate issuer is trusted for a different Honua environment."
                    : "No enabled client-certificate trust profiles are configured.",
                environmentId: environmentId,
                fingerprintSha256: fingerprint,
                issuerHash: issuerHash,
                daysUntilExpiry: daysUntilExpiry);
        }

        var issuerMatchedAnyProfile = false;
        foreach (var profile in enabledProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IssuerMatches(profile, certificate))
            {
                continue;
            }

            issuerMatchedAnyProfile = true;

            var profileFailure = ValidateProfileRules(profile, certificate, fingerprint, issuerHash, daysUntilExpiry);
            if (profileFailure is not null)
            {
                return profileFailure;
            }

            var mappingResult = TryMapPrincipal(
                profile,
                certificate,
                fingerprint,
                issuerHash,
                daysUntilExpiry,
                now);
            if (mappingResult.Succeeded ||
                mappingResult.ErrorCode == ClientCertificateValidationErrorCode.MissingIdentity)
            {
                return mappingResult;
            }
        }

        var issuerMatchesOtherEnvironment = !issuerMatchedAnyProfile &&
            await IssuerMatchesAnotherEnvironmentAsync(certificate, environmentId, cancellationToken).ConfigureAwait(false);
        var errorCode = issuerMatchedAnyProfile
            ? ClientCertificateValidationErrorCode.UnmappedIdentity
            : issuerMatchesOtherEnvironment
                ? ClientCertificateValidationErrorCode.WrongEnvironment
                : ClientCertificateValidationErrorCode.UntrustedIssuer;
        var detail = issuerMatchedAnyProfile
            ? "The client certificate identity is not mapped to a Honua principal."
            : issuerMatchesOtherEnvironment
                ? "The client certificate issuer is trusted for a different Honua environment."
                : "The client certificate issuer is not accepted by any enabled trust profile for this environment.";

        return ClientCertificateValidationResult.Failure(
            errorCode,
            detail,
            environmentId: environmentId,
            fingerprintSha256: fingerprint,
            issuerHash: issuerHash,
            daysUntilExpiry: daysUntilExpiry);
    }

    private async Task<bool> IssuerMatchesAnotherEnvironmentAsync(
        X509Certificate2 certificate,
        string environmentId,
        CancellationToken cancellationToken)
    {
        var profiles = await _trustStore.ListProfilesAsync(null, cancellationToken).ConfigureAwait(false);
        return profiles.Any(profile =>
            profile.Enabled &&
            !string.Equals(profile.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase) &&
            IssuerMatches(profile, certificate));
    }

    private static ClientCertificateValidationResult? ValidateProfileRules(
        ClientCertificateTrustProfile profile,
        X509Certificate2 certificate,
        string fingerprint,
        string issuerHash,
        int daysUntilExpiry)
    {
        if (profile.RequireClientAuthenticationEku && !HasClientAuthenticationEku(certificate))
        {
            return ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.InvalidEku,
                "The client certificate does not include the Client Authentication extended key usage.",
                profile.ProfileId,
                profile.EnvironmentId,
                fingerprint,
                issuerHash,
                daysUntilExpiry);
        }

        if (IsRevoked(profile, certificate, fingerprint))
        {
            return ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.CertificateRevoked,
                "The client certificate has been revoked for this trust profile.",
                profile.ProfileId,
                profile.EnvironmentId,
                fingerprint,
                issuerHash,
                daysUntilExpiry);
        }

        if (profile.RequireChainTrust && !ValidateChain(profile, certificate))
        {
            return ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.UntrustedChain,
                "The client certificate chain is not trusted by the configured trust profile.",
                profile.ProfileId,
                profile.EnvironmentId,
                fingerprint,
                issuerHash,
                daysUntilExpiry);
        }

        return null;
    }

    private static bool IssuerMatches(ClientCertificateTrustProfile profile, X509Certificate2 certificate)
    {
        var hasSubjects = profile.AcceptedIssuerSubjects.Count > 0;
        var hasThumbprints = profile.AcceptedIssuerThumbprints.Count > 0;
        var hasCustomAnchors = profile.CustomTrustAnchorCertificates.Count > 0;

        if (!hasSubjects && !hasThumbprints && !hasCustomAnchors)
        {
            // Options validators on both startup binding and the admin API require profiles
            // to declare at least one of these. Refuse to match a misconfigured profile
            // rather than fall through to "trust any issuer".
            return false;
        }

        if (hasSubjects)
        {
            var issuer = ClientCertificateNormalization.NormalizeThumbprintOrName(certificate.Issuer);
            if (profile.AcceptedIssuerSubjects.Any(subject =>
                string.Equals(subject, issuer, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        if (hasThumbprints || hasCustomAnchors)
        {
            return ChainMatchesIssuerHints(profile, certificate, hasThumbprints, hasCustomAnchors);
        }

        return false;
    }

    private static bool ChainMatchesIssuerHints(
        ClientCertificateTrustProfile profile,
        X509Certificate2 certificate,
        bool checkThumbprints,
        bool checkCustomAnchors)
    {
        var anchors = new List<X509Certificate2>();
        using var chain = new X509Chain();
        try
        {
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;

            if (checkCustomAnchors)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                foreach (var anchorPem in profile.CustomTrustAnchorCertificates)
                {
                    if (!TryParseTrustAnchor(anchorPem, out var parsed))
                    {
                        // A malformed configured anchor never matches; fail this profile
                        // so the validator can surface a sanitized failure instead of a 500.
                        return false;
                    }

                    anchors.Add(parsed);
                    chain.ChainPolicy.CustomTrustStore.Add(parsed);
                }
            }

            _ = chain.Build(certificate);

            if (checkCustomAnchors && !ChainTerminatesAtAnchor(chain, anchors))
            {
                return false;
            }

            if (checkThumbprints && AnyIssuerThumbprintMatches(chain, profile.AcceptedIssuerThumbprints))
            {
                return true;
            }

            return checkCustomAnchors && !checkThumbprints;
        }
        finally
        {
            foreach (var anchor in anchors)
            {
                anchor.Dispose();
            }
        }
    }

    private static bool ChainTerminatesAtAnchor(X509Chain chain, IReadOnlyList<X509Certificate2> anchors)
    {
        if (chain.ChainElements.Count == 0)
        {
            return false;
        }

        var root = chain.ChainElements[^1].Certificate;
        return anchors.Any(anchor =>
            string.Equals(anchor.Thumbprint, root.Thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AnyIssuerThumbprintMatches(X509Chain chain, IReadOnlyList<string> acceptedIssuerThumbprints)
    {
        for (var index = 1; index < chain.ChainElements.Count; index++)
        {
            var thumbprint = ClientCertificateNormalization.NormalizeThumbprintOrName(
                chain.ChainElements[index].Certificate.Thumbprint);
            if (acceptedIssuerThumbprints.Any(candidate =>
                string.Equals(candidate, thumbprint, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasClientAuthenticationEku(X509Certificate2 certificate)
    {
        var ekuExtension = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .FirstOrDefault();
        if (ekuExtension is null)
        {
            return false;
        }

        return ekuExtension.EnhancedKeyUsages
            .Cast<Oid>()
            .Any(static oid => string.Equals(oid.Value, ClientAuthenticationEku, StringComparison.Ordinal));
    }

    private static bool IsRevoked(
        ClientCertificateTrustProfile profile,
        X509Certificate2 certificate,
        string fingerprint)
    {
        var issuer = ClientCertificateNormalization.NormalizeThumbprintOrName(certificate.Issuer);
        var serial = ClientCertificateNormalization.NormalizeSerialNumber(certificate.SerialNumber);
        return profile.Revocations.Any(revocation =>
            revocation.Enabled &&
            ((!string.IsNullOrWhiteSpace(revocation.FingerprintSha256) &&
                string.Equals(revocation.FingerprintSha256, fingerprint, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(revocation.Issuer) &&
                !string.IsNullOrWhiteSpace(revocation.SerialNumber) &&
                string.Equals(revocation.Issuer, issuer, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(revocation.SerialNumber, serial, StringComparison.OrdinalIgnoreCase))));
    }

    private static bool ValidateChain(ClientCertificateTrustProfile profile, X509Certificate2 certificate)
    {
        var anchors = new List<X509Certificate2>();
        using var chain = new X509Chain();
        try
        {
            chain.ChainPolicy.RevocationMode = profile.ChainRevocationMode;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            if (profile.CustomTrustAnchorCertificates.Count > 0)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                foreach (var anchorPem in profile.CustomTrustAnchorCertificates)
                {
                    if (!TryParseTrustAnchor(anchorPem, out var parsed))
                    {
                        return false;
                    }

                    anchors.Add(parsed);
                    chain.ChainPolicy.CustomTrustStore.Add(parsed);
                }
            }

            return chain.Build(certificate);
        }
        finally
        {
            foreach (var anchor in anchors)
            {
                anchor.Dispose();
            }
        }
    }

    internal static bool TryParseTrustAnchor(string configuredCertificate, out X509Certificate2 anchor)
    {
        if (string.IsNullOrWhiteSpace(configuredCertificate))
        {
            anchor = null!;
            return false;
        }

        var value = configuredCertificate.Trim();
        try
        {
            if (value.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                anchor = X509Certificate2.CreateFromPem(value);
                return true;
            }

            anchor = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            anchor = null!;
            return false;
        }
    }

    private static ClientCertificateValidationResult TryMapPrincipal(
        ClientCertificateTrustProfile profile,
        X509Certificate2 certificate,
        string fingerprint,
        string issuerHash,
        int daysUntilExpiry,
        DateTimeOffset now)
    {
        var identities = ExtractIdentities(profile, certificate, fingerprint);
        var hasEnabledFingerprintMapping = profile.PrincipalMappings.Any(static mapping =>
            mapping.Enabled && mapping.MatchType == ClientCertificateIdentityType.FingerprintSha256);
        if (!identities.HasSanOrSubjectIdentity && !hasEnabledFingerprintMapping)
        {
            return ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.MissingIdentity,
                "The client certificate does not contain a configured SAN or subject identity.",
                profile.ProfileId,
                profile.EnvironmentId,
                fingerprint,
                issuerHash,
                daysUntilExpiry);
        }

        foreach (var mapping in profile.PrincipalMappings
                     .Where(static mapping => mapping.Enabled)
                     .OrderBy(static mapping => MatchPriority(mapping.MatchType)))
        {
            if (!IsMappingInWindow(mapping, now))
            {
                continue;
            }

            if (!identities.Contains(mapping.MatchType, mapping.MatchValue))
            {
                continue;
            }

            var principal = CreatePrincipal(profile, mapping, fingerprint);
            return ClientCertificateValidationResult.Success(
                principal,
                profile.ProfileId,
                mapping.MappingId,
                profile.EnvironmentId,
                fingerprint,
                issuerHash,
                daysUntilExpiry,
                mapping.PrincipalId);
        }

        return ClientCertificateValidationResult.Failure(
            ClientCertificateValidationErrorCode.UnmappedIdentity,
            "The client certificate identity is not mapped to a Honua principal.",
            profile.ProfileId,
            profile.EnvironmentId,
            fingerprint,
            issuerHash,
            daysUntilExpiry);
    }

    private static bool IsMappingInWindow(ClientCertificatePrincipalMapping mapping, DateTimeOffset now)
    {
        if (mapping.NotBefore.HasValue && now < mapping.NotBefore.Value)
        {
            return false;
        }

        return !mapping.NotAfter.HasValue || now <= mapping.NotAfter.Value;
    }

    private static ClaimsPrincipal CreatePrincipal(
        ClientCertificateTrustProfile profile,
        ClientCertificatePrincipalMapping mapping,
        string fingerprint)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, mapping.PrincipalId),
            new(ClaimTypes.Name, mapping.DisplayName),
            new("auth_type", "client-certificate"),
            new("honua:principal_id", mapping.PrincipalId),
            new("honua:environment_id", profile.EnvironmentId),
            new("honua:trust_profile_id", profile.ProfileId),
            new("honua:mapping_id", mapping.MappingId),
            new("honua:certificate_fingerprint_sha256", fingerprint),
            new("honua:client_certificate_valid", "true"),
        };

        foreach (var role in mapping.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (!string.IsNullOrWhiteSpace(mapping.TenantId))
        {
            claims.Add(new Claim("tenant_id", mapping.TenantId));
            claims.Add(new Claim("honua:tenant_id", mapping.TenantId));
        }

        foreach (var scope in mapping.EnvironmentScopes)
        {
            claims.Add(new Claim("honua:environment_scope", scope));
        }

        var identity = new ClaimsIdentity(
            claims,
            ClientCertificateAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private static ClientCertificateIdentitySet ExtractIdentities(
        ClientCertificateTrustProfile profile,
        X509Certificate2 certificate,
        string fingerprint)
    {
        var identities = new ClientCertificateIdentitySet();
        identities.Add(ClientCertificateIdentityType.FingerprintSha256, fingerprint);

        var san = certificate.Extensions
            .Cast<X509Extension>()
            .FirstOrDefault(static extension => string.Equals(extension.Oid?.Value, "2.5.29.17", StringComparison.Ordinal));
        if (san is not null)
        {
            AddSubjectAlternativeNames(profile, san, identities);
        }

        if (profile.AllowSubjectFallback)
        {
            identities.Add(ClientCertificateIdentityType.Subject, certificate.Subject);
        }

        return identities;
    }

    private static void AddSubjectAlternativeNames(
        ClientCertificateTrustProfile profile,
        X509Extension extension,
        ClientCertificateIdentitySet identities)
    {
        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        while (sequence.HasData)
        {
            var tag = sequence.PeekTag();
            if (tag.TagClass != TagClass.ContextSpecific)
            {
                _ = sequence.ReadEncodedValue();
                continue;
            }

            if (tag.TagValue == 1 && profile.AllowedSanTypes.Contains(ClientCertificateIdentityType.SanEmail))
            {
                identities.Add(
                    ClientCertificateIdentityType.SanEmail,
                    sequence.ReadCharacterString(
                        UniversalTagNumber.IA5String,
                        new Asn1Tag(TagClass.ContextSpecific, 1)));
                continue;
            }

            if (tag.TagValue == 2 && profile.AllowedSanTypes.Contains(ClientCertificateIdentityType.SanDns))
            {
                identities.Add(
                    ClientCertificateIdentityType.SanDns,
                    sequence.ReadCharacterString(
                        UniversalTagNumber.IA5String,
                        new Asn1Tag(TagClass.ContextSpecific, 2)));
                continue;
            }

            if (tag.TagValue == 6 && profile.AllowedSanTypes.Contains(ClientCertificateIdentityType.SanUri))
            {
                identities.Add(
                    ClientCertificateIdentityType.SanUri,
                    sequence.ReadCharacterString(
                        UniversalTagNumber.IA5String,
                        new Asn1Tag(TagClass.ContextSpecific, 6)));
                continue;
            }

            _ = sequence.ReadEncodedValue();
        }
    }

    private static int MatchPriority(ClientCertificateIdentityType matchType) => matchType switch
    {
        ClientCertificateIdentityType.FingerprintSha256 => 0,
        ClientCertificateIdentityType.SanUri => 1,
        ClientCertificateIdentityType.SanEmail => 2,
        ClientCertificateIdentityType.SanDns => 3,
        ClientCertificateIdentityType.Subject => 4,
        _ => 10,
    };

    private static string ComputeSha256Fingerprint(X509Certificate2 certificate)
    {
        var hash = SHA256.HashData(certificate.RawData);
        return Convert.ToHexString(hash);
    }

    private static string ComputeIssuerHash(string issuer)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(issuer));
        return Convert.ToHexString(hash);
    }

    private sealed class ClientCertificateIdentitySet
    {
        private readonly Dictionary<ClientCertificateIdentityType, HashSet<string>> _values = new();

        public bool HasSanOrSubjectIdentity =>
            _values.Any(pair => pair.Key is not ClientCertificateIdentityType.FingerprintSha256 && pair.Value.Count > 0);

        public void Add(ClientCertificateIdentityType type, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!_values.TryGetValue(type, out var values))
            {
                values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _values[type] = values;
            }

            values.Add(ClientCertificateNormalization.NormalizeMatchValue(type, value));
        }

        public bool Contains(ClientCertificateIdentityType type, string value)
            => _values.TryGetValue(type, out var values) &&
               values.Contains(ClientCertificateNormalization.NormalizeMatchValue(type, value));
    }
}
