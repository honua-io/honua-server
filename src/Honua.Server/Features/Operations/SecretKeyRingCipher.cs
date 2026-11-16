// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Operations;

// A random secret held outside the database protects the ring where a host cannot create a certificate.
internal sealed class SecretKeyRingMaterial
{
    internal const int MinimumSecretLength = 43;
    private const int MinimumDistinctCharacters = 10;
    private static readonly string[] _referencePrefixes = ["env:", "aws:", "azure:"];

    private static readonly byte[] _fingerprintInfo = "honua key ring secret fingerprint v1"u8.ToArray();
    private static readonly byte[] _wrapInfo = "honua key ring wrap v1"u8.ToArray();
    private readonly byte[] _inputKey;

    private SecretKeyRingMaterial(byte[] inputKey)
    {
        _inputKey = inputKey;
        Fingerprint = Convert.ToHexString(
            HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKey, outputLength: 8, salt: null, info: _fingerprintInfo));
    }

    // The fingerprint only tells a wrong secret from a damaged ring; it is derived separately from every wrapping key.
    public string Fingerprint { get; }

    public static SecretKeyRingMaterial Parse(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var trimmed = secret.Trim();

        // A reference this server did not resolve would otherwise become the key itself.
        if (_referencePrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"'{OperationSecretKeyRingProtection.SecretKey}' is a secret reference that was not resolved. "
                + "Supported references are 'env:' and 'aws:secretsmanager:'.");
        }

        if (trimmed.Length < MinimumSecretLength || trimmed.Distinct().Count() < MinimumDistinctCharacters)
        {
            throw new InvalidOperationException(
                $"'{OperationSecretKeyRingProtection.SecretKey}' must be a random secret of at least "
                + $"{MinimumSecretLength} characters (256 bits as base64url).");
        }

        return new SecretKeyRingMaterial(Encoding.UTF8.GetBytes(trimmed));
    }

    public byte[] DeriveWrappingKey(ReadOnlySpan<byte> salt)
    {
        var key = new byte[SecretKeyRingCipher.AesKeyBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _inputKey, key, salt, _wrapInfo);
        return key;
    }
}

internal sealed record OperationKeyRingProtector(X509Certificate2? Certificate, SecretKeyRingMaterial? Secret);

internal static class SecretKeyRingProtection
{
    // The dynamic dependency keeps the decryptor constructor reflectable, because native AOT otherwise drops it and every key fails.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SecretKeyRingDecryptor))]
    public static IDataProtectionBuilder ProtectKeysWith(
        this IDataProtectionBuilder builder,
        OperationKeyRingProtector protector)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(protector);
        if (protector.Secret is { } secret)
        {
            builder.Services.AddSingleton(secret);
        }

        // A certificate keeps wrapping new keys when both are configured, so an existing install's ring format does not change.
        if (protector.Certificate is { } certificate)
        {
            return builder.ProtectKeysWithRsaAesGcm(certificate);
        }

        var material = protector.Secret
            ?? throw new InvalidOperationException("The key ring needs a certificate or a secret.");
        builder.AddKeyManagementOptions(options => options.XmlEncryptor = new SecretKeyRingEncryptor(material));
        return builder;
    }
}

internal sealed class SecretKeyRingEncryptor(SecretKeyRingMaterial material) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        return new EncryptedXmlInfo(
            SecretKeyRingCipher.Encrypt(plaintextElement, material),
            typeof(SecretKeyRingDecryptor));
    }
}

public sealed class SecretKeyRingDecryptor : IXmlDecryptor
{
    private readonly SecretKeyRingMaterial _material;

    public SecretKeyRingDecryptor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _material = services.GetService(typeof(SecretKeyRingMaterial)) as SecretKeyRingMaterial
            ?? throw new InvalidOperationException(
                $"The key ring holds keys wrapped with '{OperationSecretKeyRingProtection.SecretKey}', which is not configured.");
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        return SecretKeyRingCipher.Decrypt(encryptedElement, _material);
    }
}

internal static class SecretKeyRingCipher
{
    internal const int AesKeyBytes = 32;
    private static readonly XNamespace _namespace = "urn:honua:dataprotection:hkdf-aes-gcm:v1";
    private const int SaltBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static XElement Encrypt(XElement plaintext, SecretKeyRingMaterial material)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(material);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext.ToString(SaveOptions.DisableFormatting));
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagBytes];
        var key = material.DeriveWrappingKey(salt);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, Encoding.ASCII.GetBytes(material.Fingerprint));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        return new XElement(
            _namespace + "key",
            new XElement(_namespace + "fingerprint", material.Fingerprint),
            new XElement(_namespace + "salt", Convert.ToBase64String(salt)),
            new XElement(_namespace + "nonce", Convert.ToBase64String(nonce)),
            new XElement(_namespace + "tag", Convert.ToBase64String(tag)),
            new XElement(_namespace + "ciphertext", Convert.ToBase64String(ciphertext)));
    }

    public static XElement Decrypt(XElement encrypted, SecretKeyRingMaterial material)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        ArgumentNullException.ThrowIfNull(material);
        var body = encrypted.Name == _namespace + "key"
            ? encrypted
            : encrypted.Element(_namespace + "key")
                ?? throw new CryptographicException("The key ring ciphertext is not a secret-wrapped key.");
        var fingerprint = Required(body, "fingerprint");
        if (!string.Equals(fingerprint, material.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException(
                $"The key ring was wrapped with a different '{OperationSecretKeyRingProtection.SecretKey}' "
                + $"(ring {fingerprint}, configured {material.Fingerprint}). Restore the secret the ring was created with.");
        }

        var nonce = Decode(body, "nonce");
        var tag = Decode(body, "tag");
        var ciphertext = Decode(body, "ciphertext");
        var plaintext = new byte[ciphertext.Length];
        var key = material.DeriveWrappingKey(Decode(body, "salt"));
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.ASCII.GetBytes(material.Fingerprint));
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        catch (ArgumentException exception)
        {
            throw new CryptographicException("The key ring ciphertext has a malformed field.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // DataProtection treats a key it cannot decrypt as absent and mints a new one, so a changed secret is refused before the ring is used.
    public static void EnsureReadable(IEnumerable<XElement> elements, SecretKeyRingMaterial? material)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var configured = material?.Fingerprint;
        var foreign = elements
            .SelectMany(static element => element.Descendants(_namespace + "fingerprint"))
            .Select(static fingerprint => fingerprint.Value)
            .FirstOrDefault(fingerprint => !string.Equals(fingerprint, configured, StringComparison.OrdinalIgnoreCase));
        if (foreign is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The key ring holds keys wrapped with a different '{OperationSecretKeyRingProtection.SecretKey}' "
            + $"(ring {foreign}, configured {configured ?? "none"}). Restore the secret the ring was created with. "
            + "The secret must not be rotated or regenerated.");
    }

    private static byte[] Decode(XElement body, string localName)
    {
        try
        {
            return Convert.FromBase64String(Required(body, localName));
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The key ring ciphertext has a malformed field.", exception);
        }
    }

    private static string Required(XElement body, string localName)
    {
        var value = body.Element(_namespace + localName)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CryptographicException("The key ring ciphertext is missing a required field.");
        }

        return value;
    }
}
