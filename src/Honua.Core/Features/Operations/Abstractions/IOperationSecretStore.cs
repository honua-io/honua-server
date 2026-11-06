// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

// Secret material stays outside operation handles, proposals, execution payloads, audit records, and logs.
public interface IOperationSecretStore
{
    bool IsAvailable { get; }

    OperationSecretReference Store(
        string operationInstanceId,
        string operationId,
        string? principalId,
        string? tenantId,
        string name,
        string value,
        TimeSpan? ttl = null);

    string? Consume(
        OperationSecretReference reference,
        string operationInstanceId,
        string operationId,
        string? principalId,
        string? tenantId);
}
