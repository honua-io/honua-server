// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

// Secret material stays outside operation handles, proposals, execution payloads, audit records, and logs.
public interface IOperationSecretStore
{
    bool IsAvailable { get; }

    OperationSecretReference Store(OperationSecretBinding binding, string name, string value, TimeSpan? ttl = null);

    // Returns the value once, and only to the operation, principal and tenant it was stored for.
    string? Consume(OperationSecretReference reference, OperationSecretBinding binding);
}

public sealed record OperationSecretBinding(
    string OperationInstanceId,
    string OperationId,
    string? PrincipalId,
    string? TenantId);
