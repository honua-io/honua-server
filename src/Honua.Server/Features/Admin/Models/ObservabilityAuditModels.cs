// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

internal sealed class ObservabilityAuditRecordResponse
{
    public required long AuditId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string EventType { get; init; }

    public required string Actor { get; init; }

    public required string ActorType { get; init; }

    public required string ResourceType { get; init; }

    public string? ResourceId { get; init; }

    public required string Action { get; init; }

    public required string Outcome { get; init; }

    public required string CorrelationId { get; init; }

    public string? RemoteIp { get; init; }

    public string? UserAgent { get; init; }

    public string Details { get; init; } = string.Empty;
}

internal sealed class ObservabilityAuditPageResponse
{
    public required IReadOnlyList<ObservabilityAuditRecordResponse> Items { get; init; }

    public string? NextCursor { get; init; }
}

internal sealed class ObservabilityAuditIntegrityResponse
{
    public required bool Verified { get; init; }

    public required long RowsChecked { get; init; }

    public required long UnhashedRows { get; init; }

    public long AttestedGapRows { get; init; }

    public long? FirstBrokenAuditId { get; init; }

    public string? FailureReason { get; init; }
}

internal sealed class ObservabilityAuditGapAttestationRequest
{
    // The gap starts at the verifier's first broken audit id.
    public long? GapFirstAuditId { get; init; }

    public string? Reason { get; init; }
}

internal sealed class ObservabilityAuditGapAttestationResponse
{
    public required long AttestationAuditId { get; init; }

    public required long AnchorAuditId { get; init; }

    public required long GapFirstAuditId { get; init; }

    public required long GapLastAuditId { get; init; }

    public required long GapRowCount { get; init; }

    public required long ResumeAuditId { get; init; }

    public required ObservabilityAuditIntegrityResponse Verification { get; init; }
}
