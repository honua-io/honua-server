// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Redis-backed consume-once secret channel. Redis contains only an envelope protected by
/// the shared ASP.NET data-protection key ring; the plaintext value is never a Redis value.
/// Production replay nodes must use the same persisted data-protection key ring and application
/// name so a node can resolve an envelope created by another node during key rotation.
/// </summary>
public sealed class RedisOperationSecretStore : IOperationSecretStore
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);
    private const string KeyPrefix = "controlplane:operation-secret:";
    private const string ConsumeScript = """
        local operation = redis.call('HGET', KEYS[1], 'operationInstanceId')
        local operationId = redis.call('HGET', KEYS[1], 'operationId')
        local principal = redis.call('HGET', KEYS[1], 'principalId')
        local tenant = redis.call('HGET', KEYS[1], 'tenantId')
        if operation == false or operation ~= ARGV[1] or operationId ~= ARGV[2]
            or principal ~= ARGV[3] or tenant ~= ARGV[4] then
            return false
        end
        local protectedValue = redis.call('HGET', KEYS[1], 'protectedValue')
        if protectedValue == false then
            return false
        end
        redis.call('DEL', KEYS[1])
        return protectedValue
        """;

    private readonly IDatabase _database;
    private readonly IDataProtector _protector;

    public bool IsAvailable => _database.Multiplexer.IsConnected;

    public RedisOperationSecretStore(
        IConnectionMultiplexer redis,
        IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _database = redis.GetDatabase();
        _protector = dataProtectionProvider.CreateProtector("Honua.OperationSecrets.v1");
    }

    public OperationSecretReference Store(
        string operationInstanceId,
        string operationId,
        string? principalId,
        string? tenantId,
        string name,
        string value,
        TimeSpan? ttl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var reference = new OperationSecretReference
        {
            ReferenceId = $"opsecret-{Guid.NewGuid():N}",
            Name = name,
        };
        var key = Key(reference.ReferenceId);
        var retention = ttl ?? DefaultTtl;
        _database.HashSet(key,
        [
            new HashEntry("operationInstanceId", operationInstanceId),
            new HashEntry("operationId", operationId),
            new HashEntry("principalId", principalId ?? "anonymous"),
            new HashEntry("tenantId", tenantId ?? string.Empty),
            new HashEntry("name", name),
            new HashEntry("protectedValue", _protector.Protect(value)),
        ]);
        _database.KeyExpire(key, retention);
        return reference;
    }

    public string? Consume(
        OperationSecretReference reference,
        string operationInstanceId,
        string operationId,
        string? principalId,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var result = _database.ScriptEvaluate(
            ConsumeScript,
            [Key(reference.ReferenceId)],
            [operationInstanceId, operationId, principalId ?? "anonymous", tenantId ?? string.Empty]);
        if (result.IsNull)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect((string)result!);
        }
        catch (CryptographicException)
        {
            // The atomic delete has already happened. A key-ring mismatch therefore fails
            // closed and cannot turn a replay into a second retrieval attempt.
            return null;
        }
    }

    private static string Key(string referenceId) => KeyPrefix + referenceId;
}
