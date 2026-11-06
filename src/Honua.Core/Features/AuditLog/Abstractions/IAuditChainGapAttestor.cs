// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Appends a keyed <see cref="AuditChainGapAttestation"/> for the unhashed run of rows a
/// pre-hash-chain writer appended after a binary downgrade. The attestor derives every
/// declared value from the stored rows; it never rewrites or re-hashes an existing row.
/// </summary>
public interface IAuditChainGapAttestor
{
    /// <summary>Attests the gap that starts at <see cref="AuditChainGapAttestationRequest.GapFirstAuditId"/>.</summary>
    /// <param name="request">The gap to attest and who is attesting it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome, including the verification report after the write.</returns>
    Task<AuditChainGapAttestationOutcome> AttestAsync(
        AuditChainGapAttestationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A request to attest one downgrade gap.</summary>
public sealed record AuditChainGapAttestationRequest
{
    /// <summary>The first unhashed row of the gap: the verifier's first broken <c>audit_id</c>.</summary>
    public required long GapFirstAuditId { get; init; }

    /// <summary>Why the gap exists, for example the rollback that caused it.</summary>
    public required string Reason { get; init; }

    /// <summary>The authenticated operator.</summary>
    public required string Actor { get; init; }

    /// <summary>The request correlation identifier.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>The caller's address, when known.</summary>
    public string? RemoteIp { get; init; }

    /// <summary>The caller's user agent, when known.</summary>
    public string? UserAgent { get; init; }
}

/// <summary>Result status of a gap attestation.</summary>
public enum AuditChainGapAttestationStatus
{
    /// <summary>The attestation row was written.</summary>
    Attested = 0,

    /// <summary>No chain key is configured, so no attestation could carry provenance.</summary>
    KeyNotConfigured = 1,

    /// <summary>The stored rows do not show a downgrade gap at the requested row.</summary>
    Refused = 2,

    /// <summary>The audit store did not accept the attestation row.</summary>
    WriteFailed = 3,
}

/// <summary>Outcome of <see cref="IAuditChainGapAttestor.AttestAsync"/>.</summary>
public sealed record AuditChainGapAttestationOutcome
{
    /// <summary>Result status.</summary>
    public required AuditChainGapAttestationStatus Status { get; init; }

    /// <summary>Why the request was refused, when it was.</summary>
    public string? Detail { get; init; }

    /// <summary>The written declaration, including its own <c>audit_id</c>, when attested.</summary>
    public AuditChainGapAttestation? Attestation { get; init; }

    /// <summary>The verification report after the attempt.</summary>
    public AuditIntegrityReport? Report { get; init; }
}
