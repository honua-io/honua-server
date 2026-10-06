// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using StackExchange.Redis;

namespace Honua.Server.Features.Operations;

/// <summary>Durable Redis key-ring repository shared by operation replay nodes.</summary>
internal sealed class RedisDataProtectionKeyRepository(IConnectionMultiplexer redis) : IXmlRepository
{
    private const string Key = "controlplane:operation-secret:data-protection-keys";

    /// <summary>
    /// Namespace of the <c>encryptedSecret</c> element <c>XmlKeyManager</c> decrypts.
    /// An un-namespaced marker is also accepted so the structural fixtures stay valid.
    /// Any other namespace is an unrelated marker and does not protect the key.
    /// </summary>
    private static readonly XNamespace DataProtectionNamespace = "http://schemas.asp.net/2015/03/dataProtection";

    private readonly IDatabase _database = redis.GetDatabase();

    public IReadOnlyCollection<XElement> GetAllElements()
        => _database.ListRange(Key)
            .Select(value => XElement.Parse(value.ToString(), LoadOptions.PreserveWhitespace))
            .ToArray();

    /// <summary>
    /// Rejects a legacy ring that contains any key whose descriptor was persisted without XML
    /// encryption. Existing entries are not rewritten by ProtectKeysWithCertificate, and the
    /// data-protection provider could otherwise continue selecting one of them after startup.
    /// </summary>
    public void EnsureAllElementsAreProtected()
    {
        var unprotected = GetAllElements()
            .FirstOrDefault(static element => !IsProtectedElement(element));
        if (unprotected is not null)
        {
            var keyId = unprotected.Attribute("id")?.Value ?? "unknown";
            throw new InvalidOperationException(
                $"Redis data-protection key ring contains unprotected key '{keyId}'. "
                + "Remove or migrate the legacy key before enabling operation secret storage.");
        }
    }

    internal static bool IsProtectedElement(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        // Revocation and other metadata records do not contain key material. For a key,
        // XmlKeyManager consumes descriptor's single child and decrypts an encryptedSecret
        // nested inside that child. The child itself is the inner descriptor on a real
        // ring, so requiring encryptedSecret to be that child rejects keys this process
        // just wrote. A sibling marker outside the child is ignored, and a remaining
        // masterKey means the consumed descriptor is still plaintext.
        if (element.Name != "key")
        {
            return true;
        }

        var consumed = element.Element("descriptor")?.Elements().Take(2).ToArray();
        return consumed is { Length: 1 }
            && !ContainsPlaintextSecret(consumed[0])
            && ContainsAcceptedEncryptedSecret(consumed[0]);
    }

    private static bool ContainsAcceptedEncryptedSecret(XElement node)
    {
        if (IsAcceptedEncryptedSecret(node))
        {
            return true;
        }

        foreach (var child in node.Elements())
        {
            if (ContainsAcceptedEncryptedSecret(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPlaintextSecret(XElement node)
    {
        // Ciphertext lives inside encryptedSecret. Do not read it as a plaintext master key.
        if (IsAcceptedEncryptedSecret(node))
        {
            return false;
        }

        if (node.Name.LocalName == "masterKey" || RequiresEncryption(node))
        {
            return true;
        }

        foreach (var child in node.Elements())
        {
            if (ContainsPlaintextSecret(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAcceptedEncryptedSecret(XElement node)
        => node.Name == "encryptedSecret"
            || node.Name == DataProtectionNamespace + "encryptedSecret";

    private static bool RequiresEncryption(XElement node)
    {
        var marker = node.Attribute(DataProtectionNamespace + "requiresEncryption")
            ?? node.Attribute("requiresEncryption");
        return string.Equals(marker?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        _database.ListRightPush(Key, element.ToString(SaveOptions.DisableFormatting));
    }
}
