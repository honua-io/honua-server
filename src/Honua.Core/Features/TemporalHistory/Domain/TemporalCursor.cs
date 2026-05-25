// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.TemporalHistory.Domain;

/// <summary>
/// Stable kinds of temporal cursor a client can use to address a point in a layer's history.
/// </summary>
public enum TemporalCursorKind
{
    /// <summary>
    /// An absolute UTC timestamp.
    /// </summary>
    Timestamp,

    /// <summary>
    /// A backend transaction identifier.
    /// </summary>
    Transaction,

    /// <summary>
    /// An operator-assigned named checkpoint.
    /// </summary>
    Named,

    /// <summary>
    /// A GitOps metadata release identifier.
    /// </summary>
    Release,

    /// <summary>
    /// A job-run identifier produced by the Honua job runner.
    /// </summary>
    JobRun,

    /// <summary>
    /// An edit-session correlation identifier.
    /// </summary>
    EditSession
}

/// <summary>
/// Opaque, stable cursor that addresses a point in a layer's temporal history without
/// exposing the underlying temporal-table or audit-log implementation. Cursors round-trip
/// through their textual form (for example <c>ts:2024-01-01T00:00:00.0000000Z</c> or
/// <c>named:v1.2.3</c>) so clients can persist and replay them.
/// </summary>
public sealed record TemporalCursor
{
    /// <summary>
    /// The cursor kind that selects how the reference value is resolved.
    /// </summary>
    public required TemporalCursorKind Kind { get; init; }

    /// <summary>
    /// The absolute UTC timestamp for <see cref="TemporalCursorKind.Timestamp"/> cursors.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// The opaque reference value for non-timestamp cursors (transaction, named, release, job, edit session).
    /// </summary>
    public string? Reference { get; init; }

    private const string TimestampPrefix = "ts";
    private const string TransactionPrefix = "txn";
    private const string NamedPrefix = "named";
    private const string ReleasePrefix = "release";
    private const string JobRunPrefix = "job";
    private const string EditSessionPrefix = "edit";

    /// <summary>
    /// Creates a timestamp cursor for the supplied instant, normalized to UTC.
    /// </summary>
    /// <param name="timestamp">The instant to address.</param>
    /// <returns>A timestamp cursor.</returns>
    public static TemporalCursor AtTimestamp(DateTimeOffset timestamp)
        => new() { Kind = TemporalCursorKind.Timestamp, Timestamp = timestamp.ToUniversalTime() };

    /// <summary>
    /// Attempts to parse a textual cursor token into a strongly-typed cursor without allocating
    /// intermediate substrings beyond the single reference slice.
    /// </summary>
    /// <param name="value">The textual cursor token.</param>
    /// <param name="cursor">The parsed cursor when successful.</param>
    /// <returns><see langword="true"/> when the token was recognized; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out TemporalCursor cursor)
    {
        cursor = default!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> span = value.AsSpan().Trim();
        var separator = span.IndexOf(':');
        if (separator <= 0 || separator == span.Length - 1)
        {
            return false;
        }

        var prefix = span[..separator];
        var rest = span[(separator + 1)..];

        if (prefix.Equals(TimestampPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!DateTimeOffset.TryParse(
                    rest,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return false;
            }

            cursor = AtTimestamp(parsed);
            return true;
        }

        var kind = prefix switch
        {
            _ when prefix.Equals(TransactionPrefix, StringComparison.OrdinalIgnoreCase) => TemporalCursorKind.Transaction,
            _ when prefix.Equals(NamedPrefix, StringComparison.OrdinalIgnoreCase) => TemporalCursorKind.Named,
            _ when prefix.Equals(ReleasePrefix, StringComparison.OrdinalIgnoreCase) => TemporalCursorKind.Release,
            _ when prefix.Equals(JobRunPrefix, StringComparison.OrdinalIgnoreCase) => TemporalCursorKind.JobRun,
            _ when prefix.Equals(EditSessionPrefix, StringComparison.OrdinalIgnoreCase) => TemporalCursorKind.EditSession,
            _ => (TemporalCursorKind?)null
        };

        if (kind is null)
        {
            return false;
        }

        cursor = new TemporalCursor { Kind = kind.Value, Reference = rest.ToString() };
        return true;
    }

    /// <summary>
    /// Renders the cursor as its stable textual token.
    /// </summary>
    /// <returns>The textual cursor token.</returns>
    public override string ToString()
    {
        return Kind switch
        {
            TemporalCursorKind.Timestamp =>
                $"{TimestampPrefix}:{(Timestamp ?? DateTimeOffset.UnixEpoch).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}",
            TemporalCursorKind.Transaction => $"{TransactionPrefix}:{Reference}",
            TemporalCursorKind.Named => $"{NamedPrefix}:{Reference}",
            TemporalCursorKind.Release => $"{ReleasePrefix}:{Reference}",
            TemporalCursorKind.JobRun => $"{JobRunPrefix}:{Reference}",
            TemporalCursorKind.EditSession => $"{EditSessionPrefix}:{Reference}",
            _ => $"{TimestampPrefix}:{DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture)}"
        };
    }
}
