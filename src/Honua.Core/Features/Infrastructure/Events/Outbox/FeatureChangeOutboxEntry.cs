// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Durable feature-change outbox row. <see cref="EventPayload"/> stores the serialized
/// canonical <c>FeatureChangeEventRequest</c> captured at mutation time so the dispatcher
/// can re-emit the event without re-reading the source feature (which may have been
/// mutated or deleted by a later transaction).
/// </summary>
public sealed record FeatureChangeOutboxEntry
{
    /// <summary>
    /// Stable, unique outbox row identifier. Used by the dispatcher to claim,
    /// dispatch, and mark rows. Distinct from the <c>EventId</c> stored inside
    /// <see cref="EventPayload"/>, which is the consumer-visible idempotency key.
    /// </summary>
    public required Guid OutboxId { get; init; }

    /// <summary>Service identifier the mutation was scoped to.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Layer identifier the mutation targeted.</summary>
    public required int LayerId { get; init; }

    /// <summary>Feature object identifier the mutation targeted.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Operation kind: <c>create</c>, <c>update</c>, or <c>delete</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>Originating protocol identifier (e.g. <c>rest</c>, <c>grpc</c>, <c>wfs20</c>).</summary>
    public required string Protocol { get; init; }

    /// <summary>Optional source identifier — typically equal to <see cref="Protocol"/>.</summary>
    public string? SourceId { get; init; }

    /// <summary>Originating request trace identifier for log correlation.</summary>
    public required string RequestId { get; init; }

    /// <summary>Stable consumer-visible event identifier. Re-used on dispatcher retry to
    /// preserve at-least-once-with-idempotency semantics.</summary>
    public required string EventId { get; init; }

    /// <summary>Serialized canonical event payload (full <c>FeatureChangeEventRequest</c>).
    /// Stored as <c>jsonb</c> on Postgres and <c>nvarchar(max)</c> on SQL Server.</summary>
    public required string EventPayload { get; init; }

    /// <summary>Current status — see <see cref="OutboxStatuses"/>.</summary>
    public required string Status { get; init; }

    /// <summary>Number of dispatch attempts already made.</summary>
    public required int RetryCount { get; init; }

    /// <summary>Last error message captured for an unsuccessful dispatch.</summary>
    public string? LastError { get; init; }

    /// <summary>UTC timestamp when the row was inserted.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp when the row was most recently claimed by a dispatcher.</summary>
    public DateTimeOffset? ClaimedAt { get; init; }

    /// <summary>Identifier of the dispatcher node currently holding the claim.</summary>
    public string? ClaimNodeId { get; init; }

    /// <summary>UTC timestamp when the claim lease expires.</summary>
    public DateTimeOffset? ClaimExpiresAt { get; init; }

    /// <summary>UTC timestamp when the row was dispatched successfully.</summary>
    public DateTimeOffset? DispatchedAt { get; init; }
}
