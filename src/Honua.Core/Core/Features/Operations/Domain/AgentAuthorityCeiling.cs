// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Domain;

public sealed record AgentAuthorityCeiling
{
    public string? TenantId { get; init; }

    public required string PrincipalId { get; init; }

    public IReadOnlyList<string> AllowedOperationIds { get; init; } = [];

    public OperationBlastRadiusClass MaximumBlastRadius { get; init; }

    public OperationSideEffectClass MaximumSideEffect { get; init; }

    public bool Enabled { get; init; } = true;

    public bool IsValid() => !string.IsNullOrWhiteSpace(PrincipalId) && PrincipalId.Length <= 512
        && (TenantId is null || (!string.IsNullOrWhiteSpace(TenantId) && TenantId.Length <= 256))
        && Enum.IsDefined(MaximumBlastRadius) && Enum.IsDefined(MaximumSideEffect)
        && AllowedOperationIds is { Count: <= 512 }
        && AllowedOperationIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && !id.Contains('*'));
}
