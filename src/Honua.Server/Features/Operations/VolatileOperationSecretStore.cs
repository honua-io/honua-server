// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

public sealed class VolatileOperationSecretStore : IOperationSecretStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool IsAvailable => true;

    public OperationSecretReference Store(OperationSecretBinding binding, string name, string value, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.OperationInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.OperationId);
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
                binding.OperationInstanceId,
                binding.OperationId,
                binding.PrincipalId ?? "anonymous",
                binding.TenantId ?? string.Empty,
                value,
                DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(15))));
        }

        return reference;
    }

    public string? Consume(OperationSecretReference reference, OperationSecretBinding binding)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_gate)
        {
            if (!_entries.TryGetValue(reference.ReferenceId, out var entry) ||
                entry.ExpiresAt <= DateTimeOffset.UtcNow ||
                !Matches(entry, binding))
            {
                return null;
            }

            _entries.Remove(reference.ReferenceId);
            return entry.Value;
        }
    }

    private static bool Matches(Entry entry, OperationSecretBinding binding)
        => string.Equals(entry.OperationInstanceId, binding.OperationInstanceId, StringComparison.Ordinal)
            && string.Equals(entry.OperationId, binding.OperationId, StringComparison.Ordinal)
            && string.Equals(entry.PrincipalId, binding.PrincipalId ?? "anonymous", StringComparison.Ordinal)
            && string.Equals(entry.TenantId, binding.TenantId ?? string.Empty, StringComparison.Ordinal);

    private sealed record Entry(
        string OperationInstanceId,
        string OperationId,
        string PrincipalId,
        string TenantId,
        string Value,
        DateTimeOffset ExpiresAt);
}
