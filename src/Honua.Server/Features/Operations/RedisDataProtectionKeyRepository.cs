// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using StackExchange.Redis;

namespace Honua.Server.Features.Operations;

/// <summary>Durable Redis key-ring repository shared by operation replay nodes.</summary>
internal sealed class RedisDataProtectionKeyRepository(IConnectionMultiplexer redis) : IXmlRepository
{
    private const string Key = "controlplane:operation-secret:data-protection-keys";

    /// <summary>Namespace of the encrypted-secret element <c>XmlKeyManager</c> decrypts.</summary>
    private static readonly XNamespace DataProtectionNamespace =
        "http://schemas.asp.net/2015/03/dataProtection";

    private static readonly XName KeyName = "key";
    private static readonly XName RevocationName = "revocation";
    private static readonly XName DescriptorName = "descriptor";
    private static readonly XName CreationDateName = "creationDate";
    private static readonly XName ActivationDateName = "activationDate";
    private static readonly XName ExpirationDateName = "expirationDate";
    private static readonly XName RevocationDateName = "revocationDate";
    private static readonly XName EncryptionName = "encryption";
    private static readonly XName ValidationName = "validation";
    private static readonly XName HashName = "hash";
    private static readonly XName MasterKeyName = "masterKey";
    private static readonly XName EncryptedSecretName = DataProtectionNamespace + "encryptedSecret";
    private static readonly XName IdAttributeName = "id";
    private static readonly XName VersionAttributeName = "version";
    private static readonly XName AlgorithmAttributeName = "algorithm";
    private static readonly XName DeserializerTypeAttributeName = "deserializerType";
    private static readonly XName DecryptorTypeAttributeName = "decryptorType";

    private readonly IDatabase _database = redis.GetDatabase();

    public IReadOnlyCollection<XElement> GetAllElements()
        => _database.ListRange(Key)
            .Select(value => XElement.Parse(value.ToString(), LoadOptions.PreserveWhitespace))
            .ToArray();

    /// <summary>
    /// Rejects a legacy ring that contains any key whose imported descriptor was persisted
    /// without XML encryption. <c>XmlKeyManager</c> reads only that descriptor, so an
    /// <c>encryptedSecret</c> elsewhere in the element does not protect the master key.
    /// Existing entries are not rewritten by <c>ProtectKeysWithCertificate</c>, and the
    /// data-protection provider could otherwise continue selecting one of them after startup.
    /// Revocation records carry no master key and are accepted when they match the revocation schema.
    /// </summary>
    public void EnsureAllElementsAreProtected()
    {
        var unprotected = GetAllElements()
            .FirstOrDefault(static element => !IsProtectedElement(element));
        if (unprotected is not null)
        {
            var keyId = unprotected.Attribute(IdAttributeName)?.Value ?? "unknown";
            throw new InvalidOperationException(
                $"Redis data-protection key ring contains unprotected key '{keyId}'. "
                + "Remove or migrate the legacy key before enabling operation secret storage.");
        }
    }

    /// <summary>
    /// A key is protected only when the descriptor <c>XmlKeyManager</c> imports carries an
    /// encrypted master key. A revocation record is metadata and has no descriptor.
    /// </summary>
    internal static bool IsProtectedElement(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.Name == RevocationName)
        {
            return IsRevocationRecord(element);
        }

        return element.Name == KeyName && IsProtectedKey(element);
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        _database.ListRightPush(Key, element.ToString(SaveOptions.DisableFormatting));
    }

    private static bool IsRevocationRecord(XElement element)
    {
        if (!string.Equals(element.Attribute(VersionAttributeName)?.Value, "1", StringComparison.Ordinal)
            || !TryReadDate(element, RevocationDateName, out _))
        {
            return false;
        }

        var keys = element.Elements(KeyName).ToArray();
        if (keys.Length != 1 || keys[0].Elements().Any())
        {
            return false;
        }

        var keyId = keys[0].Attribute(IdAttributeName)?.Value;
        if (string.IsNullOrWhiteSpace(keyId)
            || (!string.Equals(keyId, "*", StringComparison.Ordinal) && !Guid.TryParse(keyId, out _)))
        {
            return false;
        }

        // A revocation is not a place to store a key descriptor.
        return !element.Descendants().Any(child => child.Name == DescriptorName || child.Name == MasterKeyName);
    }

    private static bool IsProtectedKey(XElement element)
    {
        if (!Guid.TryParse(element.Attribute(IdAttributeName)?.Value, out _)
            || !string.Equals(element.Attribute(VersionAttributeName)?.Value, "1", StringComparison.Ordinal)
            || !TryReadDate(element, CreationDateName, out _)
            || !TryReadDate(element, ActivationDateName, out _)
            || !TryReadDate(element, ExpirationDateName, out _))
        {
            return false;
        }

        var descriptors = element.Elements(DescriptorName).ToArray();
        if (descriptors.Length != 1
            || string.IsNullOrWhiteSpace(descriptors[0].Attribute(DeserializerTypeAttributeName)?.Value))
        {
            return false;
        }

        // XmlKeyManager.DeserializeDescriptorFromKeyElement selects this single child and
        // ignores every sibling, including an encryptedSecret parked beside the descriptor.
        var consumed = descriptors[0].Elements().ToArray();
        if (consumed.Length != 1 || consumed[0].Name != DescriptorName)
        {
            return false;
        }

        return IsEncryptedDescriptor(consumed[0]);
    }

    private static bool IsEncryptedDescriptor(XElement descriptor)
    {
        if (descriptor.DescendantsAndSelf().Any(child => child.Name == MasterKeyName))
        {
            return false;
        }

        var algorithms = descriptor.Elements(EncryptionName).ToArray();
        if (algorithms.Length != 1
            || string.IsNullOrWhiteSpace(algorithms[0].Attribute(AlgorithmAttributeName)?.Value))
        {
            return false;
        }

        var algorithm = algorithms[0].Attribute(AlgorithmAttributeName)!.Value;
        if (!algorithm.Contains("GCM", StringComparison.Ordinal)
            && !HasAlgorithmElement(descriptor, ValidationName)
            && !HasAlgorithmElement(descriptor, HashName))
        {
            return false;
        }

        var secrets = descriptor.Descendants(EncryptedSecretName).ToArray();
        if (secrets.Length != 1 || secrets[0].Elements().Count() != 1)
        {
            return false;
        }

        var decryptorType = secrets[0].Attribute(DecryptorTypeAttributeName)?.Value;
        return IsEncryptingDecryptor(decryptorType);
    }

    private static bool HasAlgorithmElement(XElement descriptor, XName name)
    {
        var elements = descriptor.Elements(name).ToArray();
        return elements.Length == 1
            && !string.IsNullOrWhiteSpace(elements[0].Attribute(AlgorithmAttributeName)?.Value);
    }

    private static bool IsEncryptingDecryptor(string? decryptorType)
    {
        if (string.IsNullOrWhiteSpace(decryptorType)
            || decryptorType.Contains("NullXmlDecryptor", StringComparison.Ordinal))
        {
            return false;
        }

        return decryptorType.Contains("EncryptedXmlDecryptor", StringComparison.Ordinal)
            || decryptorType.Contains("RsaAesGcmKeyRingDecryptor", StringComparison.Ordinal)
            || decryptorType.Contains("DpapiNGXmlDecryptor", StringComparison.Ordinal)
            || decryptorType.Contains("DpapiXmlDecryptor", StringComparison.Ordinal);
    }

    private static bool TryReadDate(XElement parent, XName name, out DateTimeOffset value)
    {
        value = default;
        var text = parent.Element(name)?.Value;
        return !string.IsNullOrWhiteSpace(text)
            && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value);
    }
}
