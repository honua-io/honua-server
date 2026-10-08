// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Honua.Server.Features.Operations;

/// <summary>
/// The certificate Native AOT uses to encrypt the shared operation-secret key ring.
/// </summary>
/// <remarks>
/// Registered as its own type so the decryptor can resolve this certificate from the
/// application provider. A bare <see cref="X509Certificate2"/> singleton would collide
/// with any other certificate the host composes.
/// </remarks>
internal sealed class RsaAesGcmKeyRingMaterial(X509Certificate2 certificate)
{
    public X509Certificate2 Certificate { get; } = certificate;
}

/// <summary>
/// Encrypts a data-protection key with AES-GCM and wraps that key with the certificate.
/// </summary>
/// <remarks>
/// ProtectKeysWithCertificate uses EncryptedXml, which Native AOT cannot run. RSA-OAEP
/// plus AES-GCM uses the same certificate and stays on algorithms the AOT host can
/// execute, so every Lambda environment can unwrap the one ring stored in Redis.
/// </remarks>
internal sealed class RsaAesGcmKeyRingEncryptor(X509Certificate2 certificate) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        return new EncryptedXmlInfo(
            RsaAesGcmKeyRingCipher.Encrypt(plaintextElement, certificate),
            typeof(RsaAesGcmKeyRingDecryptor));
    }
}

/// <summary>Decrypts a key written by <see cref="RsaAesGcmKeyRingEncryptor"/>.</summary>
public sealed class RsaAesGcmKeyRingDecryptor : IXmlDecryptor
{
    private readonly X509Certificate2 _certificate;

    public RsaAesGcmKeyRingDecryptor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _certificate = services.GetService(typeof(RsaAesGcmKeyRingMaterial)) is RsaAesGcmKeyRingMaterial material
            ? material.Certificate
            : throw new InvalidOperationException(
                "The Native AOT key-ring certificate is not configured.");
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        return RsaAesGcmKeyRingCipher.Decrypt(encryptedElement, _certificate);
    }
}

internal static class RsaAesGcmKeyRingCipher
{
    private static readonly XNamespace Namespace = "urn:honua:dataprotection:rsa-aes-gcm:v1";
    private const int AesKeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static XElement Encrypt(XElement plaintext, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(certificate);
        using var rsa = RequireRsa(certificate, publicKey: true);
        var aesKey = new byte[AesKeyBytes];
        RandomNumberGenerator.Fill(aesKey);
        try
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext.ToString(SaveOptions.DisableFormatting));
            var nonce = new byte[NonceBytes];
            RandomNumberGenerator.Fill(nonce);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagBytes];
            using (var aes = new AesGcm(aesKey, TagBytes))
            {
                aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
            }

            var wrapped = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
            return new XElement(
                Namespace + "key",
                new XElement(Namespace + "thumbprint", certificate.Thumbprint),
                new XElement(Namespace + "wrappedKey", Convert.ToBase64String(wrapped)),
                new XElement(Namespace + "nonce", Convert.ToBase64String(nonce)),
                new XElement(Namespace + "tag", Convert.ToBase64String(tag)),
                new XElement(Namespace + "ciphertext", Convert.ToBase64String(ciphertext)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
        }
    }

    public static XElement Decrypt(XElement encrypted, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        ArgumentNullException.ThrowIfNull(certificate);
        var body = encrypted.Name == Namespace + "key"
            ? encrypted
            : encrypted.Element(Namespace + "key")
                ?? throw new CryptographicException("The key ring ciphertext is not an RSA-AES-GCM key.");
        var thumbprint = Required(body, "thumbprint");
        if (!string.Equals(thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The key ring ciphertext was wrapped for a different certificate.");
        }

        using var rsa = RequireRsa(certificate, publicKey: false);
        var aesKey = rsa.Decrypt(Convert.FromBase64String(Required(body, "wrappedKey")), RSAEncryptionPadding.OaepSHA256);
        try
        {
            var nonce = Convert.FromBase64String(Required(body, "nonce"));
            var tag = Convert.FromBase64String(Required(body, "tag"));
            var ciphertext = Convert.FromBase64String(Required(body, "ciphertext"));
            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(aesKey, TagBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
        }
    }

    private static RSA RequireRsa(X509Certificate2 certificate, bool publicKey)
    {
        var rsa = publicKey ? certificate.GetRSAPublicKey() : certificate.GetRSAPrivateKey();
        if (rsa is null)
        {
            throw new InvalidOperationException("The key ring certificate must use an RSA key.");
        }

        return rsa;
    }

    private static string Required(XElement body, string localName)
    {
        var value = body.Element(Namespace + localName)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CryptographicException("The key ring ciphertext is missing a required field.");
        }

        return value;
    }
}
