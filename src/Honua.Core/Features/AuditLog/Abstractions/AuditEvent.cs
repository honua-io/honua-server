// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// A single security-relevant audit event. Designed to be persisted to an
/// append-only sink (e.g. <c>honua.audit_log</c>) for forensics and compliance.
/// </summary>
/// <remarks>
/// <para>
/// The record is intentionally minimal and value-typed so it travels well across
/// layers without dragging in framework dependencies. Callers that need to log
/// arbitrary structured context should JSON-serialize it into
/// <see cref="Details"/> *after* sanitizing it (the audit sink does not attempt
/// to re-sanitize). Per the privacy posture of the codebase,
/// <see cref="RemoteIp"/> and <see cref="UserAgent"/> may be <c>null</c> when
/// the active deployment policy says so.
/// </para>
/// <para>
/// String fields use a sentinel rather than throwing on missing values so that
/// emit-on-failure paths in middleware never have to allocate try/catch.
/// </para>
/// </remarks>
public sealed record AuditEvent
{
    /// <summary>Sentinel used by audit emit sites when the actor cannot be identified.</summary>
    public const string AnonymousActor = "anonymous";

    /// <summary>
    /// UTC timestamp the event occurred. Should be set by the caller (not the sink)
    /// so that retries / queueing do not re-stamp the event.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Category of event — one of the well-known <see cref="AuditEventType"/> values.</summary>
    public required AuditEventType EventType { get; init; }

    /// <summary>
    /// Identifier of the actor. For <see cref="AuditActorType.UserId"/> this is the user id;
    /// for <see cref="AuditActorType.ApiKey"/> this is a hash of the api key id (never the
    /// raw key); for <see cref="AuditActorType.Anonymous"/> this is <see cref="AnonymousActor"/>;
    /// for <see cref="AuditActorType.System"/> this is the service/component name.
    /// </summary>
    public required string Actor { get; init; }

    /// <summary>Classification of the actor.</summary>
    public required AuditActorType ActorType { get; init; }

    /// <summary>
    /// Type of resource the action targeted, e.g. <c>"layer"</c>, <c>"connection"</c>,
    /// <c>"api_key"</c>, <c>"-"</c> when not applicable.
    /// </summary>
    public required string ResourceType { get; init; }

    /// <summary>Optional identifier of the specific resource (layer id, connection id, ...).</summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Action performed, dotted-lowercase by convention (e.g. <c>"auth.success"</c>,
    /// <c>"auth.failure"</c>, <c>"layer.delete"</c>, <c>"config.update"</c>).
    /// </summary>
    public required string Action { get; init; }

    /// <summary>Outcome of the action.</summary>
    public required AuditOutcome Outcome { get; init; }

    /// <summary>Correlation id (typically the request <c>X-Correlation-ID</c>) — for joining with logs/traces.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Remote IP address of the caller. May be <c>null</c> when deployment policy disables
    /// IP capture (privacy / GDPR).
    /// </summary>
    public string? RemoteIp { get; init; }

    /// <summary>User-Agent header of the caller. May be <c>null</c> or truncated.</summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Pre-sanitized structured detail. Convention is a JSON object (string), but the
    /// audit sink stores it opaquely so any short string is acceptable. Should be
    /// <see cref="string.Empty"/> rather than <c>null</c> when there is no extra context,
    /// to keep the column non-nullable in the sink schema.
    /// </summary>
    public string Details { get; init; } = string.Empty;
}
