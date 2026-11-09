// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

public interface IAuditChainGapAttestor
{
    Task<AuditChainGapAttestationOutcome> AttestAsync(
        AuditChainGapAttestationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AuditChainGapAttestationRequest
{
    public required long GapFirstAuditId { get; init; }

    public required string Reason { get; init; }

    public required string Actor { get; init; }

    public required string CorrelationId { get; init; }

    public string? RemoteIp { get; init; }

    public string? UserAgent { get; init; }
}

public enum AuditChainGapAttestationStatus
{
    Attested = 0,

    KeyNotConfigured = 1,

    Refused = 2,

    WriteFailed = 3,
}

public sealed record AuditChainGapAttestationOutcome
{
    public required AuditChainGapAttestationStatus Status { get; init; }

    public string? Detail { get; init; }

    public AuditChainGapAttestation? Attestation { get; init; }

    public AuditIntegrityReport? Report { get; init; }
}
