// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog.Export;

/// <summary>
/// A push destination for the audit trail — a SIEM/storage connector that
/// forwards batches of <see cref="AuditEvent"/> records to an external system
/// (Splunk HEC, Microsoft Sentinel, S3-compatible object storage, syslog, ...)
/// for compliance retention and security monitoring (#2157).
/// </summary>
/// <remarks>
/// <para>
/// Sinks are thin protocol adapters: they serialize the canonical
/// <see cref="AuditEvent"/> into the destination's wire format and report
/// whether the attempt succeeded and, on failure, whether a retry could
/// plausibly succeed. They MUST NOT implement their own retry/backoff,
/// dead-lettering, or data-residency policy — that is the responsibility of the
/// shared <see cref="AuditExportDispatcher"/> so behavior stays consistent
/// across every connector.
/// </para>
/// </remarks>
public interface IAuditSink
{
    /// <summary>
    /// Stable, lowercase identifier for the connector kind (e.g. <c>"splunk-hec"</c>,
    /// <c>"sentinel"</c>, <c>"s3"</c>, <c>"syslog"</c>). Used for dead-letter
    /// attribution and structured logging.
    /// </summary>
    string SinkType { get; }

    /// <summary>
    /// Geographic region the sink writes to (e.g. <c>"us-east-1"</c>,
    /// <c>"eu-west-1"</c>), or <c>null</c> when the sink has no declared region.
    /// Evaluated by <see cref="AuditResidencyGuard"/> before any data leaves the
    /// process so audit records never cross a configured data-residency boundary.
    /// </summary>
    string? Region { get; }

    /// <summary>
    /// Forwards a batch of audit events to the destination.
    /// </summary>
    /// <param name="events">The events to forward; never null or empty when invoked by the dispatcher.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="AuditSinkResult"/> describing success or the failure
    /// classification. Implementations should not throw for expected transport
    /// failures; instead they should map them to a retryable or permanent result.
    /// </returns>
    Task<AuditSinkResult> SendAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct);
}

/// <summary>
/// Outcome of an <see cref="IAuditSink.SendAsync"/> attempt.
/// </summary>
/// <remarks>
/// A failure is classified as <see cref="Retryable"/> when retrying the same
/// batch could plausibly succeed (transport timeouts, HTTP 5xx, HTTP 429, socket
/// errors). Permanent failures (HTTP 4xx other than 429, malformed configuration)
/// are not retried and flow straight to the dead-letter store.
/// </remarks>
public sealed record AuditSinkResult
{
    /// <summary>Whether the batch was accepted by the destination.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Whether a failed attempt may be retried. Always <c>false</c> when
    /// <see cref="Succeeded"/> is <c>true</c>.
    /// </summary>
    public required bool Retryable { get; init; }

    /// <summary>
    /// A short, non-sensitive failure description suitable for logs and the
    /// dead-letter record. <c>null</c> on success.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Creates a successful result.</summary>
    /// <returns>A success result.</returns>
    public static AuditSinkResult Success() => new() { Succeeded = true, Retryable = false };

    /// <summary>Creates a retryable (transient) failure result.</summary>
    /// <param name="error">A short, non-sensitive failure description.</param>
    /// <returns>A retryable failure result.</returns>
    public static AuditSinkResult TransientFailure(string error)
        => new() { Succeeded = false, Retryable = true, Error = error };

    /// <summary>Creates a non-retryable (permanent) failure result.</summary>
    /// <param name="error">A short, non-sensitive failure description.</param>
    /// <returns>A permanent failure result.</returns>
    public static AuditSinkResult PermanentFailure(string error)
        => new() { Succeeded = false, Retryable = false, Error = error };
}
