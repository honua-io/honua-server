// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>Process-local consume-once secret channel for development and tests.</summary>
public sealed class VolatileOperationSecretStore : IOperationSecretStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool IsAvailable => true;

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
        lock (_gate)
        {
            _entries.Add(reference.ReferenceId, new Entry(
                operationInstanceId,
                operationId,
                principalId ?? "anonymous",
                tenantId ?? string.Empty,
                value,
                DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(15))));
        }

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
        lock (_gate)
        {
            if (!_entries.TryGetValue(reference.ReferenceId, out var entry) ||
                entry.ExpiresAt <= DateTimeOffset.UtcNow ||
                !Matches(entry, operationInstanceId, operationId, principalId, tenantId))
            {
                return null;
            }

            _entries.Remove(reference.ReferenceId);
            return entry.Value;
        }
    }

    private static bool Matches(
        Entry entry,
        string operationInstanceId,
        string operationId,
        string? principalId,
        string? tenantId)
        => string.Equals(entry.OperationInstanceId, operationInstanceId, StringComparison.Ordinal)
            && string.Equals(entry.OperationId, operationId, StringComparison.Ordinal)
            && string.Equals(entry.PrincipalId, principalId ?? "anonymous", StringComparison.Ordinal)
            && string.Equals(entry.TenantId, tenantId ?? string.Empty, StringComparison.Ordinal);

    private sealed record Entry(
        string OperationInstanceId,
        string OperationId,
        string PrincipalId,
        string TenantId,
        string Value,
        DateTimeOffset ExpiresAt);
}
