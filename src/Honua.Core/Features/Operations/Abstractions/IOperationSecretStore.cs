// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Consume-once channel for operation secret material. Implementations must keep the value
/// outside operation handles, proposals, execution payloads, audit records, and logs.
/// </summary>
public interface IOperationSecretStore
{
    /// <summary>Whether the channel is available before an operation is actuated.</summary>
    bool IsAvailable { get; }

    /// <summary>Stores secret material and returns an opaque reference.</summary>
    OperationSecretReference Store(
        string operationInstanceId,
        string operationId,
        string? principalId,
        string? tenantId,
        string name,
        string value,
        TimeSpan? ttl = null);

    /// <summary>
    /// Atomically consumes a reference when its operation, tenant, and principal match.
    /// Returns null for an unknown, expired, unauthorized, or already-consumed reference.
    /// </summary>
    string? Consume(
        OperationSecretReference reference,
        string operationInstanceId,
        string operationId,
        string? principalId,
        string? tenantId);
}
