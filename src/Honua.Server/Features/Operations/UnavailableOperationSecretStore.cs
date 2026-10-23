// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>Fail-closed secret channel used when the durable Redis authority is unavailable.</summary>
internal sealed class UnavailableOperationSecretStore : IOperationSecretStore
{
    public bool IsAvailable => false;

    private static InvalidOperationException Unavailable()
        => new("The operation secret channel is unavailable.");

    public OperationSecretReference Store(
        string operationInstanceId,
        string operationId,
        string? principalId,
        string? tenantId,
        string name,
        string value,
        TimeSpan? ttl = null)
        => throw Unavailable();

    public string? Consume(
        OperationSecretReference reference,
        string operationInstanceId,
        string operationId,
        string? principalId,
        string? tenantId)
        => null;
}
