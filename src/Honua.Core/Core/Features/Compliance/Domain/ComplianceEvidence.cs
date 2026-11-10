// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

// Evidence is collected from server state and is never user-authored.
public sealed record ComplianceEvidence
{
    public required string ControlId { get; init; }

    public required DateTimeOffset CollectedAt { get; init; }

    public required string Source { get; init; }

    public required string Claim { get; init; }

    public required ComplianceControlStatus Status { get; init; }

    public string Detail { get; init; } = string.Empty;
}
