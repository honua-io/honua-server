// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.Observability.Domain;

/// <summary>
/// Severity of a deterministic ops finding, ordered from lowest to highest operational urgency.
/// </summary>
public enum OpsFindingSeverity
{
    /// <summary>Informational: an observation an operator may want to know; no urgency.</summary>
    Info = 0,

    /// <summary>Warning: a condition worth attention that may degrade service if unaddressed.</summary>
    Warning = 1,

    /// <summary>Critical: an active operational problem requiring prompt operator action.</summary>
    Critical = 2,
}

/// <summary>
/// The subject a finding is about: a small set of stable identifiers pinning the finding to a
/// concrete operational entity. Only the identifiers relevant to the emitting rule are populated;
/// unset identifiers are omitted from serialized output.
/// </summary>
public sealed record OpsFindingSubject
{
    /// <summary>Gets the deploy target identifier this finding concerns, when applicable.</summary>
    public string? TargetId { get; init; }

    /// <summary>Gets the execution workload / batch-compute backend identifier, when applicable.</summary>
    public string? WorkloadId { get; init; }

    /// <summary>Gets the notification/alert channel identifier this finding concerns, when applicable.</summary>
    public string? Channel { get; init; }

    /// <summary>Gets the workflow/deploy operation identifier this finding concerns, when applicable.</summary>
    public string? OperationId { get; init; }

    /// <summary>Gets the platform release version this finding concerns, when applicable.</summary>
    public string? ReleaseVersion { get; init; }

    /// <summary>
    /// Produces a canonical, order-stable key over the populated identifiers, used to derive the
    /// finding's deterministic identifier. The format is fixed so the same condition instance always
    /// hashes to the same value.
    /// </summary>
    /// <returns>A canonical subject key.</returns>
    public string ToCanonicalKey()
        => string.Join(
            '|',
            $"target={TargetId}",
            $"workload={WorkloadId}",
            $"channel={Channel}",
            $"operation={OperationId}",
            $"release={ReleaseVersion}");
}

/// <summary>
/// A recommended, approval-gated fix for a finding. Materializing it produces an operation-gateway
/// request; the gateway either executes it directly or (far more commonly) creates an approval
/// proposal. The reasoning about whether to act stays client-side/operator-side — this record only
/// carries the deterministic, ready-to-route payload.
/// </summary>
public sealed record OpsFindingRecommendedAction
{
    /// <summary>Gets the operation class the gateway routes this action through.</summary>
    public required OperationClass Kind { get; init; }

    /// <summary>Gets a short, plain-language summary of what the action does.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Gets the opaque, class-specific execution payload (JSON) that the matching gateway executor
    /// understands. It is passed through verbatim; findings never interpret it.
    /// </summary>
    public required string ExecutionPayload { get; init; }

    /// <summary>Gets the operator-facing reason recorded on the resulting gateway request/proposal.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// A single deterministic operational finding produced by the ops-findings engine. Findings are
/// computed on demand from live signals; no model/LLM reasoning is involved. A finding with a
/// <see cref="RecommendedAction"/> can be proposed through the approval gateway.
/// </summary>
public sealed record OpsFinding
{
    /// <summary>
    /// Gets the stable identifier for this finding, deterministic per condition instance
    /// (a hash of the rule id and subject); the same live condition always yields the same id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Gets the kebab-case rule identifier that produced this finding (e.g. <c>platform-release-skew</c>).</summary>
    public required string Rule { get; init; }

    /// <summary>Gets the finding severity.</summary>
    public required OpsFindingSeverity Severity { get; init; }

    /// <summary>Gets a short, human-readable title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets a plain-language, deterministic explanation of the condition and its meaning.</summary>
    public required string Explanation { get; init; }

    /// <summary>Gets the UTC time at which the condition was detected (the evaluation time).</summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>Gets the subject identifiers the finding pins to.</summary>
    public required OpsFindingSubject Subject { get; init; }

    /// <summary>
    /// Gets the supporting evidence references (operate-feed event ids, metric names, or endpoint
    /// paths an operator can follow to corroborate the finding).
    /// </summary>
    public required IReadOnlyList<string> EvidenceRefs { get; init; }

    /// <summary>
    /// Gets the recommended, approval-gated fix, or <c>null</c> when the finding is informational
    /// and has no safe automatic action.
    /// </summary>
    public OpsFindingRecommendedAction? RecommendedAction { get; init; }
}

/// <summary>
/// Outcome of materializing a finding's recommended action through the operation gateway.
/// </summary>
public enum OpsFindingProposalStatus
{
    /// <summary>The finding no longer exists (the underlying condition cleared before proposing).</summary>
    FindingNotFound = 0,

    /// <summary>The finding exists but carries no recommended action to propose.</summary>
    NoRecommendedAction = 1,

    /// <summary>The gateway executed the action directly (no approval required).</summary>
    Executed = 2,

    /// <summary>The gateway created an approval proposal; see <see cref="OpsFindingProposalResult.ProposalId"/>.</summary>
    ProposalCreated = 3,

    /// <summary>The gateway blocked the action.</summary>
    Blocked = 4,

    /// <summary>The gateway does not support the action's operation class.</summary>
    NotSupported = 5,

    /// <summary>
    /// The durable control-plane operation gateway is unavailable, so the recommended action cannot
    /// be routed. This is the degraded outcome when the server runs without its durable backend
    /// (Redis): findings are still evaluated and returned, but proposing their fixes requires the
    /// gateway, which is only wired when the durable control-plane graph is registered.
    /// </summary>
    GatewayUnavailable = 6,
}

/// <summary>
/// Result of a propose-through-gateway request for a finding's recommended action.
/// </summary>
public sealed record OpsFindingProposalResult
{
    /// <summary>Gets the proposal outcome status.</summary>
    public required OpsFindingProposalStatus Status { get; init; }

    /// <summary>Gets the finding identifier the request targeted.</summary>
    public required string FindingId { get; init; }

    /// <summary>Gets the created proposal identifier when <see cref="Status"/> is <see cref="OpsFindingProposalStatus.ProposalCreated"/>.</summary>
    public string? ProposalId { get; init; }

    /// <summary>Gets the executed operation identifier when <see cref="Status"/> is <see cref="OpsFindingProposalStatus.Executed"/>.</summary>
    public string? ExecutionOperationId { get; init; }

    /// <summary>Gets an operator-facing message describing the outcome, when available.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Derives deterministic finding identifiers so the same live condition always maps to the same id
/// across evaluations (enabling idempotent propose-through-gateway routing keyed on the id).
/// </summary>
public static class OpsFindingId
{
    /// <summary>
    /// Computes a stable identifier from a rule id and a finding subject.
    /// </summary>
    /// <param name="rule">The kebab-case rule identifier.</param>
    /// <param name="subject">The finding subject.</param>
    /// <returns>A deterministic, URL-safe finding identifier.</returns>
    public static string Create(string rule, OpsFindingSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Create(rule, subject.ToCanonicalKey());
    }

    /// <summary>
    /// Computes a stable identifier from a rule id and a pre-computed canonical subject key.
    /// </summary>
    /// <param name="rule">The kebab-case rule identifier.</param>
    /// <param name="subjectKey">The canonical subject key.</param>
    /// <returns>A deterministic, URL-safe finding identifier.</returns>
    public static string Create(string rule, string subjectKey)
    {
        var material = $"{rule} {subjectKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        // 128 bits of the digest as lowercase hex is ample to avoid collisions while staying compact.
        return $"{rule}-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
    }
}
