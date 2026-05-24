// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Wire shape for an audit event row (#1168).
/// </summary>
internal sealed class ObservabilityAuditRecordResponse
{
    /// <summary>Sink-assigned audit row identifier.</summary>
    public required long AuditId { get; init; }

    /// <summary>Original event timestamp.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Event type category.</summary>
    public required string EventType { get; init; }

    /// <summary>Actor identifier.</summary>
    public required string Actor { get; init; }

    /// <summary>Actor classification.</summary>
    public required string ActorType { get; init; }

    /// <summary>Resource type, "-" when not applicable.</summary>
    public required string ResourceType { get; init; }

    /// <summary>Optional resource identifier.</summary>
    public string? ResourceId { get; init; }

    /// <summary>Dotted action name.</summary>
    public required string Action { get; init; }

    /// <summary>Outcome classification.</summary>
    public required string Outcome { get; init; }

    /// <summary>Correlation id captured at emit time.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Remote IP address when capture is enabled.</summary>
    public string? RemoteIp { get; init; }

    /// <summary>User-Agent header when capture is enabled.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Pre-sanitized structured detail (JSON text or short marker).</summary>
    public string Details { get; init; } = string.Empty;
}

/// <summary>
/// Page of audit records.
/// </summary>
internal sealed class ObservabilityAuditPageResponse
{
    /// <summary>Items returned in this page (newest first).</summary>
    public required IReadOnlyList<ObservabilityAuditRecordResponse> Items { get; init; }

    /// <summary>Cursor for the next page or null when none.</summary>
    public string? NextCursor { get; init; }
}
