// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

/// <summary>
/// Validates a SAML 2.0 Response/Assertion and projects it to a normalized identity (#508).
/// </summary>
public interface ISamlAssertionValidator
{
    /// <summary>
    /// Validates the supplied SAML Response XML (already base64-decoded). On success returns a
    /// <see cref="SamlValidationResult"/> with <see cref="SamlValidationResult.Succeeded"/> set
    /// and the extracted subject/attributes; otherwise a failure result with a reason.
    /// </summary>
    SamlValidationResult Validate(string responseXml);
}

/// <summary>The outcome of validating a SAML Response.</summary>
public sealed class SamlValidationResult
{
    private SamlValidationResult(bool succeeded, string? failureReason, SamlSubject? subject)
    {
        Succeeded = succeeded;
        FailureReason = failureReason;
        Subject = subject;
    }

    /// <summary>Whether validation succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>Why validation failed (null on success). Safe, non-sensitive summary.</summary>
    public string? FailureReason { get; }

    /// <summary>The validated subject (null on failure).</summary>
    public SamlSubject? Subject { get; }

    /// <summary>Creates a successful result.</summary>
    public static SamlValidationResult Success(SamlSubject subject) => new(true, null, subject);

    /// <summary>Creates a failure result with a non-sensitive reason.</summary>
    public static SamlValidationResult Failure(string reason) => new(false, reason, null);
}

/// <summary>The normalized subject extracted from a validated SAML assertion.</summary>
public sealed class SamlSubject
{
    /// <summary>The subject NameID (stable user identifier).</summary>
    public required string NameId { get; init; }

    /// <summary>Display name, if present.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Email, if present.</summary>
    public string? Email { get; init; }

    /// <summary>Roles mapped from the configured role attribute.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>The assertion's session-expiry instant, if specified.</summary>
    public DateTimeOffset? SessionNotOnOrAfter { get; init; }
}

/// <summary>
/// Default <see cref="ISamlAssertionValidator"/>. Verifies the enveloped XML-DSig signature
/// against the configured IdP certificate using only <see cref="System.Security.Cryptography"/>
/// primitives and a hand-rolled exclusive XML canonicalizer — deliberately avoiding
/// <c>System.Security.Cryptography.Xml.SignedXml</c>, which is not part of the in-box shared
/// framework and is not AOT/trim-friendly. Enforces signature presence, digest match, issuer,
/// audience, and the <c>NotBefore</c>/<c>NotOnOrAfter</c> conditions with bounded clock skew.
/// </summary>
internal sealed class SamlAssertionValidator(IOptions<SamlAuthenticationOptions> options) : ISamlAssertionValidator
{
    private const string DsigNs = "http://www.w3.org/2000/09/xmldsig#";
    private const string SamlAssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string SamlProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";

    private readonly SamlAuthenticationOptions _options = options.Value;

    public SamlValidationResult Validate(string responseXml)
    {
        if (string.IsNullOrWhiteSpace(responseXml))
        {
            return SamlValidationResult.Failure("Empty SAML response.");
        }

        if (string.IsNullOrWhiteSpace(_options.IdpSigningCertificate))
        {
            return SamlValidationResult.Failure("No IdP signing certificate configured.");
        }

        XDocument document;
        try
        {
            document = SecureXmlDocumentParser.Parse(responseXml, LoadOptions.None);
        }
        catch (XmlException)
        {
            return SamlValidationResult.Failure("Malformed SAML XML.");
        }

        var root = document.Root;
        if (root is null)
        {
            return SamlValidationResult.Failure("Missing SAML root element.");
        }

        // Status must be Success (when a protocol Response envelope is present).
        if (root.Name.LocalName == "Response")
        {
            var statusCode = root
                .Element(XName.Get("Status", SamlProtocolNs))?
                .Element(XName.Get("StatusCode", SamlProtocolNs))?
                .Attribute("Value")?.Value;
            if (statusCode is not null && !statusCode.EndsWith(":status:Success", StringComparison.Ordinal))
            {
                return SamlValidationResult.Failure("SAML Response status is not Success.");
            }
        }

        var assertion = root.Name.LocalName == "Assertion"
            ? root
            : root.Element(XName.Get("Assertion", SamlAssertionNs));
        if (assertion is null)
        {
            return SamlValidationResult.Failure("No SAML Assertion element present.");
        }

        // ---- Signature: presence + cryptographic verification (forged/unsigned => reject).
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(_options.IdpSigningCertificate!.Trim()));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return SamlValidationResult.Failure("IdP signing certificate is not valid base64 DER.");
        }

        using (certificate)
        {
            var signatureResult = SamlSignatureVerifier.Verify(assertion, certificate);
            if (!signatureResult.Verified)
            {
                return SamlValidationResult.Failure(signatureResult.Reason ?? "SAML signature verification failed.");
            }
        }

        // ---- Issuer.
        var issuer = assertion.Element(XName.Get("Issuer", SamlAssertionNs))?.Value?.Trim();
        if (!string.IsNullOrEmpty(_options.IdpEntityId) &&
            !string.Equals(issuer, _options.IdpEntityId, StringComparison.Ordinal))
        {
            return SamlValidationResult.Failure("SAML assertion issuer does not match the configured IdP.");
        }

        var now = DateTimeOffset.UtcNow;
        var skew = TimeSpan.FromSeconds(Math.Max(0, _options.AllowedClockSkewSeconds));

        // ---- Conditions: NotBefore / NotOnOrAfter + AudienceRestriction.
        var conditions = assertion.Element(XName.Get("Conditions", SamlAssertionNs));
        if (conditions is not null)
        {
            if (TryParseInstant(conditions.Attribute("NotBefore")?.Value, out var notBefore) &&
                now + skew < notBefore)
            {
                return SamlValidationResult.Failure("SAML assertion is not yet valid (NotBefore).");
            }

            if (TryParseInstant(conditions.Attribute("NotOnOrAfter")?.Value, out var notOnOrAfter) &&
                now - skew >= notOnOrAfter)
            {
                return SamlValidationResult.Failure("SAML assertion has expired (NotOnOrAfter).");
            }

            if (!string.IsNullOrEmpty(_options.EntityId))
            {
                var audiences = conditions
                    .Elements(XName.Get("AudienceRestriction", SamlAssertionNs))
                    .SelectMany(ar => ar.Elements(XName.Get("Audience", SamlAssertionNs)))
                    .Select(a => a.Value.Trim())
                    .ToList();

                if (audiences.Count > 0 &&
                    !audiences.Any(a => string.Equals(a, _options.EntityId, StringComparison.Ordinal)))
                {
                    return SamlValidationResult.Failure("SAML assertion audience does not match this SP.");
                }
            }
        }

        // ---- Subject.
        var subjectElement = assertion.Element(XName.Get("Subject", SamlAssertionNs));
        var nameId = subjectElement?.Element(XName.Get("NameID", SamlAssertionNs))?.Value?.Trim();
        // The subjectElement is null check is redundant in practice (a null subjectElement
        // always yields a null/empty nameId above too) but makes subjectElement's non-null
        // state after this guard provable to the compiler, avoiding a redundant `?.` below.
        if (string.IsNullOrEmpty(nameId) || subjectElement is null)
        {
            return SamlValidationResult.Failure("SAML assertion has no Subject NameID.");
        }

        // ---- SubjectConfirmationData NotOnOrAfter (bearer expiry).
        var confirmationData = subjectElement
            .Element(XName.Get("SubjectConfirmation", SamlAssertionNs))?
            .Element(XName.Get("SubjectConfirmationData", SamlAssertionNs));
        if (confirmationData is not null &&
            TryParseInstant(confirmationData.Attribute("NotOnOrAfter")?.Value, out var subjectExpiry) &&
            now - skew >= subjectExpiry)
        {
            return SamlValidationResult.Failure("SAML subject confirmation has expired.");
        }

        // ---- Attributes -> roles / email / display name.
        var attributes = ReadAttributes(assertion);
        var roles = attributes.TryGetValue(_options.RoleAttribute, out var roleValues)
            ? roleValues.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        if (roles.Count == 0 && !string.IsNullOrWhiteSpace(_options.DefaultRole))
        {
            roles = [_options.DefaultRole!.Trim()];
        }

        var email = attributes.TryGetValue(_options.EmailAttribute, out var emailValues)
            ? emailValues.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim()
            : null;
        var displayName = attributes.TryGetValue(_options.DisplayNameAttribute, out var nameValues)
            ? nameValues.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim()
            : null;

        DateTimeOffset? sessionExpiry = null;
        var authnStatement = assertion.Element(XName.Get("AuthnStatement", SamlAssertionNs));
        if (authnStatement is not null &&
            TryParseInstant(authnStatement.Attribute("SessionNotOnOrAfter")?.Value, out var parsedSession))
        {
            sessionExpiry = parsedSession;
        }

        return SamlValidationResult.Success(new SamlSubject
        {
            NameId = nameId,
            Email = email,
            DisplayName = displayName,
            Roles = roles,
            SessionNotOnOrAfter = sessionExpiry,
        });
    }

    private static Dictionary<string, List<string>> ReadAttributes(XElement assertion)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var statement in assertion.Elements(XName.Get("AttributeStatement", SamlAssertionNs)))
        {
            foreach (var attribute in statement.Elements(XName.Get("Attribute", SamlAssertionNs)))
            {
                var name = attribute.Attribute("Name")?.Value;
                var friendlyName = attribute.Attribute("FriendlyName")?.Value;
                var values = attribute
                    .Elements(XName.Get("AttributeValue", SamlAssertionNs))
                    .Select(v => v.Value)
                    .ToList();

                AddAttribute(result, name, values);
                AddAttribute(result, friendlyName, values);
            }
        }

        return result;
    }

    private static void AddAttribute(Dictionary<string, List<string>> result, string? key, List<string> values)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!result.TryGetValue(key, out var existing))
        {
            existing = [];
            result[key] = existing;
        }

        existing.AddRange(values);
    }

    private static bool TryParseInstant(string? value, out DateTimeOffset instant)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out instant))
        {
            return true;
        }

        instant = default;
        return false;
    }
}
