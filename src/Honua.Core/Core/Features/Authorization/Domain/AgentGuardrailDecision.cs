// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Authorization.Domain;

public sealed record AgentGuardrailDecision
{
    public required AgentGuardrailLevel Level { get; init; }

    public required HonuaEdition Edition { get; init; }

    public bool RequiresValidation { get; init; }

    public bool RequiresSnapshot { get; init; }

    public bool RequiresApproval { get; init; }

    public bool RequiresPolicyAudit { get; init; }

    public bool SnapshotUnsupported { get; init; }

    public IReadOnlyList<string> ReasonCodes { get; init; } = [];

    public bool HasGuardrails =>
        RequiresValidation || RequiresSnapshot || RequiresApproval || RequiresPolicyAudit;
}
