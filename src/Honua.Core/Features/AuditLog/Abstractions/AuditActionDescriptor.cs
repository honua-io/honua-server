// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Declarative description of an audited operation. Produced by an
/// <see cref="IAuditActionResolver"/> from the matched route + HTTP method and
/// consumed by the audit middleware to construct an <see cref="AuditEvent"/>
/// without per-endpoint code.
/// </summary>
/// <remarks>
/// <para>
/// The descriptor is the data-plane representation of a single row in the audit
/// coverage matrix (see <c>docs/operator/audit-coverage-matrix.md</c>). It binds
/// a recognized operation to its stable <see cref="AuditEventType"/>, dotted
/// <see cref="Action"/> verb, and the resource family the operation targets.
/// Outcome and actor are resolved per-request by the middleware (from the final
/// response status code and the authenticated principal) rather than baked into
/// the descriptor, so a single descriptor covers both success and failure.
/// </para>
/// </remarks>
public sealed record AuditActionDescriptor
{
    /// <summary>Category of the audited event.</summary>
    public required AuditEventType EventType { get; init; }

    /// <summary>
    /// Dotted-lowercase action verb recorded on the audit event
    /// (e.g. <c>"admin.config.update"</c>, <c>"feature.delete"</c>,
    /// <c>"auth.success"</c>). Stable across releases so downstream forensic
    /// tooling can filter on it.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Resource family the operation targets (e.g. <c>"admin"</c>,
    /// <c>"layer"</c>, <c>"feature"</c>, <c>"http"</c>). Maps to
    /// <see cref="AuditEvent.ResourceType"/>.
    /// </summary>
    public required string ResourceType { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the middleware emits an audit event even when
    /// the operation succeeds with a 2xx response. When <see langword="false"/>,
    /// the operation is only audited on a security-relevant failure
    /// (401/403/5xx) — used for read paths that should not flood the audit sink
    /// on every successful request.
    /// </summary>
    public bool AuditOnSuccess { get; init; } = true;
}
