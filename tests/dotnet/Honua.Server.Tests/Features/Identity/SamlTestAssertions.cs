// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Honua.Server.Tests.Features.Identity;

/// <summary>
/// Builds SAML 2.0 Response/Assertion XML signed with the in-box <see cref="SignedXml"/>
/// reference implementation. This is the independent oracle for the server's hand-rolled,
/// AOT-safe verifier (#508): if the server accepts what Microsoft's signer produced, the
/// canonicalization and signature paths are interoperable. Test-only — no shipped project
/// references <c>System.Security.Cryptography.Xml</c>.
/// </summary>
internal static class SamlTestAssertions
{
    public const string Issuer = "https://idp.example.com/metadata";
    public const string Audience = "https://honua.example.com/sp";

    /// <summary>Assertion consumer service URL the fixtures and the SP options agree on.</summary>
    public const string AcsUrl = Audience + "/saml/acs";

    /// <summary>Creates a self-signed RSA certificate to act as the IdP signing certificate.</summary>
    public static X509Certificate2 CreateSigningCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Honua SAML Test IdP",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>The base64 DER of the certificate (the value the server stores as its IdP cert).</summary>
    public static string ToBase64Der(X509Certificate2 certificate)
        => Convert.ToBase64String(certificate.Export(X509ContentType.Cert));

    /// <summary>
    /// Produces a signed, valid SAML Response (base64-encoded, ready for the ACS SAMLResponse form field).
    /// </summary>
    public static string CreateSignedResponse(
        X509Certificate2 certificate,
        string nameId,
        string email,
        string displayName,
        IReadOnlyList<string> roles,
        DateTimeOffset? notOnOrAfter = null,
        DateTimeOffset? notBefore = null,
        string? recipient = null,
        string? responseInResponseTo = null,
        string? confirmationInResponseTo = null,
        bool includeConditions = true,
        bool includeAudience = true,
        bool includeNotOnOrAfter = true,
        bool includeDestination = true)
    {
        var xml = BuildResponseXml(
            nameId,
            email,
            displayName,
            roles,
            notBefore,
            notOnOrAfter,
            recipient,
            responseInResponseTo,
            confirmationInResponseTo,
            includeConditions,
            includeAudience,
            includeNotOnOrAfter,
            includeDestination);
        var signed = SignAssertion(xml, certificate);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(signed));
    }

    /// <summary>
    /// Signed response whose <c>saml</c>, <c>xs</c>, and <c>xsi</c> prefixes are declared only
    /// on the Response. Keycloak and SimpleSAMLphp emit this shape; exclusive C14N must keep
    /// those ancestor declarations when the assertion is detached for the digest.
    /// </summary>
    public static string CreateSignedPrefixedAncestorResponse(X509Certificate2 certificate, string nameId)
    {
        const string saml = "urn:oasis:names:tc:SAML:2.0:assertion";
        const string samlp = "urn:oasis:names:tc:SAML:2.0:protocol";
        const string xs = "http://www.w3.org/2001/XMLSchema";
        const string xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var now = DateTimeOffset.UtcNow;
        var noa = now.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var nb = now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var issueInstant = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var xml = $"""
            <samlp:Response xmlns:samlp="{samlp}" xmlns:saml="{saml}" xmlns:xs="{xs}" xmlns:xsi="{xsi}" ID="_respPrefixed" Version="2.0" IssueInstant="{issueInstant}" Destination="{AcsUrl}">
              <saml:Issuer>{Issuer}</saml:Issuer>
              <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status>
              <saml:Assertion ID="_assertionPrefixed" Version="2.0" IssueInstant="{issueInstant}">
                <saml:Issuer>{Issuer}</saml:Issuer>
                <saml:Subject>
                  <saml:NameID Format="urn:oasis:names:tc:SAML:2.0:nameid-format:persistent">{nameId}</saml:NameID>
                  <saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer">
                    <saml:SubjectConfirmationData NotOnOrAfter="{noa}" Recipient="{AcsUrl}"/>
                  </saml:SubjectConfirmation>
                </saml:Subject>
                <saml:Conditions NotBefore="{nb}" NotOnOrAfter="{noa}">
                  <saml:AudienceRestriction><saml:Audience>{Audience}</saml:Audience></saml:AudienceRestriction>
                </saml:Conditions>
                <saml:AttributeStatement>
                  <saml:Attribute Name="Role">
                    <saml:AttributeValue xsi:type="xs:string">viewer</saml:AttributeValue>
                  </saml:Attribute>
                </saml:AttributeStatement>
              </saml:Assertion>
            </samlp:Response>
            """;
        var signed = SignElement(xml, "_assertionPrefixed", certificate);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(signed));
    }

    /// <summary>
    /// Produces an UNSIGNED SAML Response (base64-encoded). Used to prove the server rejects it.
    /// </summary>
    public static string CreateUnsignedResponse(string nameId)
    {
        var xml = BuildResponseXml(nameId, "x@example.com", "X", ["viewer"], null, null);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
    }

    /// <summary>
    /// Produces a signed response whose assertion body is then tampered with (an attribute value
    /// changed AFTER signing). The signature digest must no longer match.
    /// </summary>
    public static string CreateTamperedResponse(X509Certificate2 certificate, string nameId)
    {
        var xml = BuildResponseXml(nameId, "x@example.com", "X", ["viewer"], null, null);
        var signed = SignAssertion(xml, certificate);
        // Tamper: flip the role value in the signed XML.
        var tampered = signed.Replace(">viewer<", ">admin<", StringComparison.Ordinal);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered));
    }

    /// <summary>
    /// Produces a signed SAML Response whose <c>AttributeStatement</c> carries the supplied
    /// attribute <c>Name</c>/values verbatim. This drives the per-IdP conformance matrix
    /// (#2154): each IdP (Okta, Entra ID, Auth0, PingFederate) emits role/email/display-name
    /// under different attribute <c>Name</c> URIs, and Honua maps them through the configured
    /// <c>RoleAttribute</c>/<c>EmailAttribute</c>/<c>DisplayNameAttribute</c>. The signature is
    /// produced by the in-box <see cref="SignedXml"/> oracle exactly as for the default builder.
    /// </summary>
    public static string CreateSignedResponseWithAttributes(
        X509Certificate2 certificate,
        string nameId,
        IReadOnlyList<(string Name, IReadOnlyList<string> Values)> attributes,
        DateTimeOffset? notOnOrAfter = null,
        DateTimeOffset? notBefore = null)
    {
        var xml = BuildResponseXml(nameId, attributes, notBefore, notOnOrAfter);
        var signed = SignAssertion(xml, certificate);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(signed));
    }

    /// <summary>
    /// Produces a signed SAML 2.0 <c>LogoutRequest</c> (base64-encoded, HTTP-POST binding) as an
    /// IdP emits for IdP-initiated Single Logout (#2154). The <c>LogoutRequest</c> element is
    /// signed with the in-box <see cref="SignedXml"/> oracle so the server's hand-rolled verifier
    /// must accept it.
    /// </summary>
    public static string CreateSignedLogoutRequest(X509Certificate2 certificate, string nameId)
    {
        var xml = BuildLogoutRequestXml(nameId);
        var signed = SignElement(xml, "_logout1", certificate);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(signed));
    }

    /// <summary>
    /// Produces an UNSIGNED SAML 2.0 <c>LogoutRequest</c> (base64-encoded). Used to prove the
    /// server rejects an unauthenticated single-logout that would otherwise terminate a session.
    /// </summary>
    public static string CreateUnsignedLogoutRequest(string nameId)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildLogoutRequestXml(nameId)));

    private static string BuildLogoutRequestXml(string nameId)
    {
        const string saml = "urn:oasis:names:tc:SAML:2.0:assertion";
        const string samlp = "urn:oasis:names:tc:SAML:2.0:protocol";
        var issueInstant = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        // Realistic IdP shape: a prefixed samlp:LogoutRequest root with samlp-prefixed and
        // assertion-default-namespace children, exercising exclusive-C14N prefix rendering.
        return $"""
            <samlp:LogoutRequest xmlns:samlp="{samlp}" xmlns="{saml}" ID="_logout1" Version="2.0" IssueInstant="{issueInstant}">
              <Issuer>{Issuer}</Issuer>
              <NameID Format="urn:oasis:names:tc:SAML:2.0:nameid-format:persistent">{nameId}</NameID>
              <samlp:SessionIndex>_session1</samlp:SessionIndex>
            </samlp:LogoutRequest>
            """;
    }

    private static string BuildResponseXml(
        string nameId,
        string email,
        string displayName,
        IReadOnlyList<string> roles,
        DateTimeOffset? notBefore,
        DateTimeOffset? notOnOrAfter,
        string? recipient = null,
        string? responseInResponseTo = null,
        string? confirmationInResponseTo = null,
        bool includeConditions = true,
        bool includeAudience = true,
        bool includeNotOnOrAfter = true,
        bool includeDestination = true)
        => BuildResponseXml(
            nameId,
            new (string, IReadOnlyList<string>)[]
            {
                ("email", new[] { email }),
                ("displayName", new[] { displayName }),
                ("Role", roles),
            },
            notBefore,
            notOnOrAfter,
            recipient,
            responseInResponseTo,
            confirmationInResponseTo,
            includeConditions,
            includeAudience,
            includeNotOnOrAfter,
            includeDestination);

    private static string BuildResponseXml(
        string nameId,
        IReadOnlyList<(string Name, IReadOnlyList<string> Values)> attributes,
        DateTimeOffset? notBefore,
        DateTimeOffset? notOnOrAfter,
        string? recipient = null,
        string? responseInResponseTo = null,
        string? confirmationInResponseTo = null,
        bool includeConditions = true,
        bool includeAudience = true,
        bool includeNotOnOrAfter = true,
        bool includeDestination = true)
    {
        const string saml = "urn:oasis:names:tc:SAML:2.0:assertion";
        const string samlp = "urn:oasis:names:tc:SAML:2.0:protocol";

        var now = DateTimeOffset.UtcNow;
        var nb = (notBefore ?? now.AddMinutes(-5)).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var noa = (notOnOrAfter ?? now.AddMinutes(5)).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var issueInstant = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var acs = recipient ?? AcsUrl;
        var destinationAttr = includeDestination ? $" Destination=\"{AcsUrl}\"" : string.Empty;
        var responseInResponseToAttr = responseInResponseTo is null ? string.Empty : $" InResponseTo=\"{responseInResponseTo}\"";
        var confirmationInResponseToAttr = confirmationInResponseTo is null
            ? string.Empty
            : $" InResponseTo=\"{confirmationInResponseTo}\"";
        var notOnOrAfterAttr = includeNotOnOrAfter ? $" NotOnOrAfter=\"{noa}\"" : string.Empty;
        var audienceXml = includeAudience
            ? $"<AudienceRestriction><Audience>{Audience}</Audience></AudienceRestriction>"
            : string.Empty;
        var conditionsXml = includeConditions
            ? $"""
                <Conditions NotBefore="{nb}"{notOnOrAfterAttr}>
                  {audienceXml}
                </Conditions>
                """
            : string.Empty;

        var attributesXml = new StringBuilder();
        foreach (var (name, values) in attributes)
        {
            attributesXml.Append(CultureInfo.InvariantCulture, $"<Attribute Name=\"{name}\">");
            foreach (var value in values)
            {
                attributesXml.Append(CultureInfo.InvariantCulture, $"<AttributeValue>{value}</AttributeValue>");
            }

            attributesXml.Append("</Attribute>");
        }

        return $"""
            <samlp:Response xmlns:samlp="{samlp}" xmlns="{saml}" ID="_resp1" Version="2.0" IssueInstant="{issueInstant}"{destinationAttr}{responseInResponseToAttr}>
              <Issuer>{Issuer}</Issuer>
              <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status>
              <Assertion ID="_assertion1" Version="2.0" IssueInstant="{issueInstant}">
                <Issuer>{Issuer}</Issuer>
                <Subject>
                  <NameID Format="urn:oasis:names:tc:SAML:2.0:nameid-format:persistent">{nameId}</NameID>
                  <SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer">
                    <SubjectConfirmationData NotOnOrAfter="{noa}" Recipient="{acs}"{confirmationInResponseToAttr}/>
                  </SubjectConfirmation>
                </Subject>
                {conditionsXml}
                <AuthnStatement AuthnInstant="{issueInstant}">
                  <AuthnContext><AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:Password</AuthnContextClassRef></AuthnContext>
                </AuthnStatement>
                <AttributeStatement>
                  {attributesXml}
                </AttributeStatement>
              </Assertion>
            </samlp:Response>
            """;
    }

    /// <summary>
    /// Signs the &lt;Assertion&gt; element with an enveloped signature using Exclusive C14N and
    /// RSA-SHA256, mirroring what a real IdP emits.
    /// </summary>
    private static string SignAssertion(string responseXml, X509Certificate2 certificate)
        => SignElement(responseXml, "_assertion1", certificate);

    /// <summary>
    /// Signs the element bearing the supplied <c>ID</c> with an enveloped XML-DSig signature
    /// (Exclusive C14N, RSA-SHA256), inserting the signature after the element's saml:Issuer
    /// child. Shared by the assertion and LogoutRequest signers.
    /// </summary>
    private static string SignElement(string xml, string referenceId, X509Certificate2 certificate)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);

        var nsmgr = new XmlNamespaceManager(document.NameTable);
        nsmgr.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
        var element = (XmlElement)document.SelectSingleNode($"//*[@ID='{referenceId}']")!;

        using var rsa = certificate.GetRSAPrivateKey()!;
        var signedXml = new SignedXml(element) { SigningKey = rsa };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;

        var reference = new Reference("#" + referenceId);
        reference.DigestMethod = SignedXml.XmlDsigSHA256Url;
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);

        signedXml.ComputeSignature();
        var signatureElement = signedXml.GetXml();

        // Insert the signature as a direct child after the Issuer; the verifier locates it as a
        // direct child regardless of position.
        element.InsertAfter(signatureElement, element.SelectSingleNode("saml:Issuer", nsmgr));

        return document.OuterXml;
    }
}
