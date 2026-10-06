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
        // XmlKeyManager consumes the descriptor element's single child, so only an
        // encryptedSecret in that position proves that the consumed descriptor is protected.
        if (element.Name != "key")
        {
            return true;
        }

        var descriptorPayload = element.Element("descriptor")?.Elements().Take(2).ToArray();
        return descriptorPayload is { Length: 1 }
            && descriptorPayload[0].Name == "encryptedSecret";
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        _database.ListRightPush(Key, element.ToString(SaveOptions.DisableFormatting));
    }
}
