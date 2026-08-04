// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Streaming.Conformance;

/// <summary>
/// Mutation operations the controlled workflow accepts.
/// </summary>
internal static class FeatureStreamConformanceOperations
{
    /// <summary>Create one controlled record owned by the run.</summary>
    public const string Insert = "insert";

    /// <summary>Update one of the run's own controlled records to a new label.</summary>
    public const string Update = "update";

    /// <summary>
    /// Rewrite one of the run's own controlled records with its current values. The record's
    /// state is unchanged but the canonical edit pipeline still publishes a change event, so
    /// two subscriptions opened at different times observe an identical baseline and an
    /// identical mutation. That is what lets a cross-transport conformance run compare
    /// normalized state across transports it can only open sequentially.
    /// </summary>
    public const string Touch = "touch";

    /// <summary>Delete one of the run's own controlled records.</summary>
    public const string Delete = "delete";

    /// <summary>Every accepted operation, in the order they are advertised.</summary>
    public static readonly string[] All = [Insert, Update, Touch, Delete];

    /// <summary>
    /// Normalizes a caller-supplied operation. Returns null for anything unrecognized so an
    /// unknown operation fails closed instead of defaulting to a mutation the caller did not
    /// ask for.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        foreach (var operation in All)
        {
            if (trimmed.Equals(operation, StringComparison.OrdinalIgnoreCase))
            {
                return operation;
            }
        }

        return null;
    }
}

/// <summary>
/// Ownership marker written into a controlled record's <c>RunIdField</c>.
/// </summary>
/// <remarks>
/// The marker is self-describing — prefix, run id, and absolute expiry — so the TTL sweeper
/// can reclaim orphaned records using only what is stored on the row. That matters because
/// the case the sweeper exists for is exactly the case where the in-memory lease registry is
/// gone: the process that created the record died (NFR-001).
/// </remarks>
internal readonly record struct FeatureStreamConformanceMarker(Guid RunId, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Marker prefix. Also the discriminator the sweeper uses to tell controlled records
    /// apart from anything else that may live in the source, so a malformed or foreign value
    /// is left alone rather than deleted.
    /// </summary>
    public const string Prefix = "honua-conformance";

    private const char Separator = ':';

    /// <summary>Renders the marker in its stored form.</summary>
    public string Format()
        => string.Join(
            Separator,
            Prefix,
            RunId.ToString("N"),
            ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Parses a stored marker. Returns false for anything this server did not write, which
    /// is what keeps the sweeper from deleting a record it does not own.
    /// </summary>
    public static bool TryParse(object? stored, out FeatureStreamConformanceMarker marker)
    {
        marker = default;
        if (stored is not string text || text.Length == 0)
        {
            return false;
        }

        var parts = text.Split(Separator);
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Guid.TryParseExact(parts[1], "N", out var runId))
        {
            return false;
        }

        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresAtUnix))
        {
            return false;
        }

        marker = new FeatureStreamConformanceMarker(runId, DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix));
        return true;
    }
}

/// <summary>
/// Why a controlled-conformance request was refused. Every value is a fail-closed outcome:
/// the workflow never degrades into mutating something else.
/// </summary>
internal enum FeatureStreamConformanceFailure
{
    /// <summary>The request succeeded.</summary>
    None = 0,

    /// <summary>The deployment does not provision a controlled-conformance source.</summary>
    Disabled,

    /// <summary>The configured source could not be resolved, or is not writable.</summary>
    SourceUnavailable,

    /// <summary>The deployment reports no immutable revision to bind evidence to.</summary>
    DeploymentRevisionUnavailable,

    /// <summary>The caller's expected deployment revision does not match this deployment.</summary>
    DeploymentRevisionMismatch,

    /// <summary>The caller's expected source identity does not match the configured source.</summary>
    SourceIdentityMismatch,

    /// <summary>Every lease is currently held.</summary>
    LeaseUnavailable,

    /// <summary>No live run matches the supplied identity and token.</summary>
    RunNotFound,

    /// <summary>The run exhausted its mutation budget.</summary>
    MutationBudgetExhausted,

    /// <summary>The run holds its maximum number of controlled records.</summary>
    RecordBudgetExhausted,

    /// <summary>The targeted record is not owned by this run.</summary>
    RecordNotOwned,

    /// <summary>The request itself was malformed.</summary>
    InvalidRequest
}

/// <summary>
/// Outcome of a controlled-conformance operation.
/// </summary>
/// <typeparam name="T">Payload type on success.</typeparam>
internal readonly record struct FeatureStreamConformanceResult<T>(
    FeatureStreamConformanceFailure Failure,
    string? Message,
    T? Value)
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess => Failure == FeatureStreamConformanceFailure.None;

    /// <summary>Creates a success result.</summary>
    public static FeatureStreamConformanceResult<T> Success(T value)
        => new(FeatureStreamConformanceFailure.None, null, value);

    /// <summary>Creates a failure result.</summary>
    public static FeatureStreamConformanceResult<T> Fail(FeatureStreamConformanceFailure failure, string message)
        => new(failure, message, default);
}

/// <summary>
/// Request body for acquiring a conformance run lease.
/// </summary>
internal sealed record FeatureStreamConformanceRunRequest
{
    /// <summary>Optional caller label recorded on the lease and on controlled records.</summary>
    public string? ClientLabel { get; init; }

    /// <summary>
    /// Deployment revision the caller believes it is reviewing. When supplied it must match
    /// this deployment exactly, so a run that was scheduled against one image can never
    /// silently produce evidence against another (REQ-006).
    /// </summary>
    public string? ExpectedDeploymentRevision { get; init; }

    /// <summary>
    /// Conformance service the caller believes it is mutating. When supplied it must match
    /// the configured source exactly.
    /// </summary>
    public string? ExpectedServiceId { get; init; }

    /// <summary>Requested lease TTL in seconds. Clamped to the configured bounds.</summary>
    public int? TtlSeconds { get; init; }
}

/// <summary>
/// Response describing a leased conformance run.
/// </summary>
internal sealed record FeatureStreamConformanceRunResponse
{
    /// <summary>Run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Bearer token proving ownership of this run. Presented on every subsequent request as
    /// <c>X-Honua-Conformance-Run-Token</c>. Returned exactly once, at lease time.
    /// </summary>
    public required string RunToken { get; init; }

    /// <summary>
    /// Value written into <see cref="RunIdField"/> on this run's controlled records. Also
    /// the correlation a subscriber matches on to recognize its own mutations.
    /// </summary>
    public required string RunMarker { get; init; }

    /// <summary>Dedicated conformance service identifier.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Dedicated conformance layer identifier.</summary>
    public required int LayerId { get; init; }

    /// <summary>Attribute carrying the ownership marker.</summary>
    public required string RunIdField { get; init; }

    /// <summary>When the lease expires and the run becomes sweepable.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Remaining mutation budget.</summary>
    public required int RemainingMutations { get; init; }

    /// <summary>Maximum controlled records this run may hold at once.</summary>
    public required int MaxRecords { get; init; }

    /// <summary>Immutable deployment revision this run is bound to.</summary>
    public required string DeploymentRevision { get; init; }

    /// <summary>
    /// Content digest of the source's immutable baseline — every record in the conformance
    /// source that no run owns — at lease time. Comparing it against the digest returned by
    /// cleanup proves the run left the baseline exactly as it found it.
    /// </summary>
    public required string BaselineDigest { get; init; }

    /// <summary>Number of records the baseline digest covers.</summary>
    public required int BaselineRecordCount { get; init; }
}

/// <summary>
/// Request body for one controlled mutation.
/// </summary>
internal sealed record FeatureStreamConformanceMutationRequest
{
    /// <summary>Operation to perform. See <see cref="FeatureStreamConformanceOperations"/>.</summary>
    public string? Operation { get; init; }

    /// <summary>
    /// Target record for <c>update</c>, <c>touch</c>, and <c>delete</c>. Must be a record
    /// this run owns.
    /// </summary>
    public long? ObjectId { get; init; }

    /// <summary>Optional label written to the record's label attribute.</summary>
    public string? Label { get; init; }
}

/// <summary>
/// Result of one controlled mutation.
/// </summary>
internal sealed record FeatureStreamConformanceMutationResponse
{
    /// <summary>Run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Operation performed.</summary>
    public required string Operation { get; init; }

    /// <summary>Record the mutation targeted or created.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Ordinal of this mutation within the run, starting at 1.</summary>
    public required int MutationOrdinal { get; init; }

    /// <summary>Remaining mutation budget after this mutation.</summary>
    public required int RemainingMutations { get; init; }

    /// <summary>Controlled records this run currently holds.</summary>
    public required int OwnedRecords { get; init; }

    /// <summary>Ownership marker written to the record, echoed for subscriber correlation.</summary>
    public required string RunMarker { get; init; }
}

/// <summary>
/// Result of releasing a conformance run.
/// </summary>
internal sealed record FeatureStreamConformanceCleanupResponse
{
    /// <summary>Run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Controlled records deleted by this call.</summary>
    public required int DeletedRecords { get; init; }

    /// <summary>Baseline digest after cleanup.</summary>
    public required string BaselineDigest { get; init; }

    /// <summary>Number of records the baseline digest covers.</summary>
    public required int BaselineRecordCount { get; init; }

    /// <summary>
    /// Whether the conformance source now holds no controlled records at all — the strongest
    /// statement cleanup can make, and the one an operator wants after a failed run.
    /// </summary>
    public required bool BaselineRestored { get; init; }
}

/// <summary>
/// Result of an operator-initiated reset of the conformance source.
/// </summary>
internal sealed record FeatureStreamConformanceResetResponse
{
    /// <summary>Leases dropped.</summary>
    public required int ReleasedRuns { get; init; }

    /// <summary>Controlled records deleted.</summary>
    public required int DeletedRecords { get; init; }

    /// <summary>Baseline digest after the reset.</summary>
    public required string BaselineDigest { get; init; }

    /// <summary>Number of records the baseline digest covers.</summary>
    public required int BaselineRecordCount { get; init; }
}
