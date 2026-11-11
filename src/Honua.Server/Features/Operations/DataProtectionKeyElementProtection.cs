// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Server.Features.Operations;

internal static class DataProtectionKeyElementProtection
{
    private static readonly XNamespace _dataProtectionNamespace = "http://schemas.asp.net/2015/03/dataProtection";

    // Configuring encryption does not rewrite existing entries, and a plaintext key is refused because the provider can still select it.
    public static void EnsureAllProtected(IEnumerable<XElement> elements)
    {
        var unprotected = elements.FirstOrDefault(static element => !IsProtected(element));
        if (unprotected is not null)
        {
            var keyId = unprotected.Attribute("id")?.Value ?? "unknown";
            throw new InvalidOperationException(
                $"The data-protection key ring contains unprotected key '{keyId}'. "
                + "Remove the key before enabling operation secret storage.");
        }
    }

    public static bool IsProtected(XElement element)
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
        => IsAcceptedEncryptedSecret(node) || node.Elements().Any(ContainsAcceptedEncryptedSecret);

    private static bool ContainsPlaintextSecret(XElement node)
    {
        // Ciphertext lives inside encryptedSecret. Do not read it as a plaintext master key.
        if (IsAcceptedEncryptedSecret(node))
        {
            return false;
        }

        return node.Name.LocalName == "masterKey"
            || RequiresEncryption(node)
            || node.Elements().Any(ContainsPlaintextSecret);
    }

    private static bool IsAcceptedEncryptedSecret(XElement node)
        => node.Name == "encryptedSecret"
            || node.Name == _dataProtectionNamespace + "encryptedSecret";

    private static bool RequiresEncryption(XElement node)
    {
        var marker = node.Attribute(_dataProtectionNamespace + "requiresEncryption")
            ?? node.Attribute("requiresEncryption");
        return string.Equals(marker?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
