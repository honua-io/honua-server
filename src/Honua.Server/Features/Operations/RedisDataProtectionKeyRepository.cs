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

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        _database.ListRightPush(Key, element.ToString(SaveOptions.DisableFormatting));
    }
}
