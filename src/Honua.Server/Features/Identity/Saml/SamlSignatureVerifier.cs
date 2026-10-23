// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace Honua.Server.Features.Identity.Saml;

/// <summary>
/// Verifies an enveloped XML-DSig signature on a SAML assertion using only
/// <see cref="System.Security.Cryptography"/> primitives plus a focused Exclusive XML
/// Canonicalization (C14N, RFC 3741) implementation. This avoids
/// <c>System.Security.Cryptography.Xml.SignedXml</c>, which is not in the in-box shared
/// framework and is hostile to AOT/trimming.
/// </summary>
/// <remarks>
/// Security scope: supports the standard SAML SP profile — a single enveloped signature over
/// the assertion, the Enveloped-Signature transform, Exclusive C14N, SHA-256/SHA-1 digests,
/// and RSA-SHA256/RSA-SHA1 (and ECDSA equivalents) signatures. Both the digest of the signed
/// element AND the signature over <c>SignedInfo</c> are checked, the signed element is pinned
/// to the assertion by ID, and the signing key must be the configured IdP certificate's key.
/// </remarks>
internal static class SamlSignatureVerifier
{
    private const string DsigNs = "http://www.w3.org/2000/09/xmldsig#";

    internal readonly record struct VerificationResult(bool Verified, string? Reason)
    {
        public static VerificationResult Ok() => new(true, null);

        public static VerificationResult Fail(string reason) => new(false, reason);
    }

    public static VerificationResult Verify(XElement assertion, X509Certificate2 certificate)
    {
        var signature = assertion.Element(XName.Get("Signature", DsigNs));
        if (signature is null)
        {
            // No enveloped signature on the assertion => treat as unsigned and reject.
            return VerificationResult.Fail("SAML assertion is not signed.");
        }

        var signedInfo = signature.Element(XName.Get("SignedInfo", DsigNs));
        if (signedInfo is null)
        {
            return VerificationResult.Fail("Signature is missing SignedInfo.");
        }

        var reference = signedInfo.Element(XName.Get("Reference", DsigNs));
        if (reference is null)
        {
            return VerificationResult.Fail("SignedInfo is missing a Reference.");
        }

        // ---- 1. Bind the reference URI to the assertion element by ID.
        var referenceUri = reference.Attribute("URI")?.Value ?? string.Empty;
        var assertionId = assertion.Attribute("ID")?.Value;
        if (string.IsNullOrEmpty(assertionId))
        {
            return VerificationResult.Fail("Signed assertion has no ID attribute.");
        }

        // The reference must target the assertion itself (URI="" means the enclosing document
        // root, URI="#id" targets that element). Reject signatures that point elsewhere so a
        // wrapped/foreign signed fragment cannot be substituted.
        if (referenceUri.Length != 0 &&
            !string.Equals(referenceUri, "#" + assertionId, StringComparison.Ordinal))
        {
            return VerificationResult.Fail("Signature reference does not target the assertion.");
        }

        // ---- 2. Recompute the digest of the assertion (enveloped-signature transform: the
        //         Signature element is removed before canonicalization) and compare.
        var digestMethod = reference
            .Element(XName.Get("DigestMethod", DsigNs))?
            .Attribute("Algorithm")?.Value;
        var expectedDigest = reference.Element(XName.Get("DigestValue", DsigNs))?.Value?.Trim();
        if (string.IsNullOrEmpty(digestMethod) || string.IsNullOrEmpty(expectedDigest))
        {
            return VerificationResult.Fail("Reference is missing DigestMethod/DigestValue.");
        }

        var inclusivePrefixesForReference = ReadInclusiveNamespacePrefixes(reference);

        // Build a detached copy of the assertion with its own Signature removed (enveloped).
        // new XElement drops ancestor namespace declarations. Exclusive C14N then renders a
        // prefixed assertion (Keycloak / SimpleSAMLphp, where xmlns:saml and xmlns:xs live
        // only on the Response) as if those prefixes were undeclared, and the digest fails.
        var assertionCopy = new XElement(assertion);
        CopyInScopeNamespaceDeclarations(assertion, assertionCopy);
        assertionCopy.Element(XName.Get("Signature", DsigNs))?.Remove();

        var canonicalAssertion = ExclusiveCanonicalizer.Canonicalize(assertionCopy, inclusivePrefixesForReference);
        var actualDigest = ComputeDigest(digestMethod, canonicalAssertion);
        if (actualDigest is null)
        {
            return VerificationResult.Fail("Unsupported digest algorithm.");
        }

        if (!FixedTimeEqualsBase64(expectedDigest, actualDigest))
        {
            return VerificationResult.Fail("SAML assertion digest mismatch (tampered or wrapped).");
        }

        // ---- 3. Verify the signature over the canonicalized SignedInfo.
        var signatureMethod = signedInfo
            .Element(XName.Get("SignatureMethod", DsigNs))?
            .Attribute("Algorithm")?.Value;
        var signatureValue = signature.Element(XName.Get("SignatureValue", DsigNs))?.Value;
        if (string.IsNullOrEmpty(signatureMethod) || string.IsNullOrWhiteSpace(signatureValue))
        {
            return VerificationResult.Fail("Signature is missing SignatureMethod/SignatureValue.");
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signatureValue.Trim());
        }
        catch (FormatException)
        {
            return VerificationResult.Fail("SignatureValue is not valid base64.");
        }

        var hashAlgorithm = ResolveSignatureHash(signatureMethod);
        if (hashAlgorithm is null)
        {
            return VerificationResult.Fail("Unsupported signature algorithm.");
        }

        var inclusivePrefixesForSignedInfo = ReadInclusiveNamespacePrefixes(signedInfo);
        // SignedInfo must be canonicalized in the context of its ancestors (namespaces in
        // scope), per Exclusive C14N — pass the live node so inherited namespaces resolve.
        var canonicalSignedInfo = ExclusiveCanonicalizer.Canonicalize(signedInfo, inclusivePrefixesForSignedInfo);

        var verified = VerifySignature(certificate, signatureMethod, hashAlgorithm.Value, canonicalSignedInfo, signatureBytes);
        return verified
            ? VerificationResult.Ok()
            : VerificationResult.Fail("SAML SignedInfo signature is invalid.");
    }

    /// <summary>
    /// Copies namespace declarations that are in scope on <paramref name="source"/> but not
    /// already present on <paramref name="copy"/>. Nearer ancestors win. Exclusive C14N emits
    /// only visibly utilized prefixes and InclusiveNamespaces entries, so unused declarations
    /// seeded here do not change the digest.
    /// </summary>
    private static void CopyInScopeNamespaceDeclarations(XElement source, XElement copy)
    {
        for (var ancestor = source.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            foreach (var attribute in ancestor.Attributes())
            {
                if (!attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                var alreadyDeclared = false;
                foreach (var existing in copy.Attributes())
                {
                    if (existing.IsNamespaceDeclaration && existing.Name == attribute.Name)
                    {
                        alreadyDeclared = true;
                        break;
                    }
                }

                if (!alreadyDeclared)
                {
                    copy.Add(new XAttribute(attribute));
                }
            }
        }
    }

    private static byte[]? ComputeDigest(string algorithm, byte[] data)
    {
        return algorithm switch
        {
            "http://www.w3.org/2001/04/xmlenc#sha256" => SHA256.HashData(data),
            "http://www.w3.org/2001/04/xmldsig-more#sha384" => SHA384.HashData(data),
            "http://www.w3.org/2001/04/xmlenc#sha512" => SHA512.HashData(data),
#pragma warning disable CA5350 // SHA-1 retained only for interop with legacy IdPs that still emit it.
            "http://www.w3.org/2000/09/xmldsig#sha1" => SHA1.HashData(data),
#pragma warning restore CA5350
            _ => null,
        };
    }

    private static HashAlgorithmName? ResolveSignatureHash(string algorithm)
    {
        return algorithm switch
        {
            "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256" => HashAlgorithmName.SHA256,
            "http://www.w3.org/2001/04/xmldsig-more#rsa-sha384" => HashAlgorithmName.SHA384,
            "http://www.w3.org/2001/04/xmldsig-more#rsa-sha512" => HashAlgorithmName.SHA512,
            "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256" => HashAlgorithmName.SHA256,
            "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha384" => HashAlgorithmName.SHA384,
            "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha512" => HashAlgorithmName.SHA512,
#pragma warning disable CA5350 // SHA-1 retained only for interop with legacy IdPs that still emit it.
            "http://www.w3.org/2000/09/xmldsig#rsa-sha1" => HashAlgorithmName.SHA1,
            "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha1" => HashAlgorithmName.SHA1,
#pragma warning restore CA5350
            _ => null,
        };
    }

    private static bool VerifySignature(
        X509Certificate2 certificate,
        string signatureMethod,
        HashAlgorithmName hashAlgorithm,
        byte[] canonicalSignedInfo,
        byte[] signatureBytes)
    {
        if (signatureMethod.Contains("ecdsa", StringComparison.OrdinalIgnoreCase))
        {
            using var ecdsa = certificate.GetECDsaPublicKey();
            return ecdsa is not null &&
                ecdsa.VerifyData(canonicalSignedInfo, signatureBytes, hashAlgorithm, DSASignatureFormat.Rfc3279DerSequence);
        }

        using var rsa = certificate.GetRSAPublicKey();
        return rsa is not null &&
            rsa.VerifyData(canonicalSignedInfo, signatureBytes, hashAlgorithm, RSASignaturePadding.Pkcs1);
    }

    private static List<string> ReadInclusiveNamespacePrefixes(XElement signedElementParent)
    {
        // The CanonicalizationMethod/Transform may carry an <ec:InclusiveNamespaces
        // PrefixList="..."/> child. Collect prefixes from any such descendant.
        var prefixes = new List<string>();
        foreach (var list in signedElementParent.Descendants()
                     .Where(e => e.Name.LocalName == "InclusiveNamespaces")
                     .Select(e => e.Attribute("PrefixList")?.Value)
                     .Where(list => !string.IsNullOrWhiteSpace(list)))
        {
            prefixes.AddRange(list!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return prefixes;
    }

    private static bool FixedTimeEqualsBase64(string expectedBase64, byte[] actual)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(expectedBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
