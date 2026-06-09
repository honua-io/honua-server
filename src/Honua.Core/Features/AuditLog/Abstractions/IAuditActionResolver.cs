// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Maps a matched HTTP route (method + route pattern) to an
/// <see cref="AuditActionDescriptor"/> so the audit middleware can decide,
/// centrally and declaratively, which operations are audited.
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of truth that keeps audit emission
/// <i>middleware-driven</i> rather than scattered across endpoint handlers. The
/// route pattern passed in is the raw ASP.NET Core route template of the matched
/// endpoint (e.g. <c>"/api/v{version:apiVersion}/admin/connections/{id}"</c>),
/// which is stable regardless of the concrete request path, so the resolver does
/// not have to canonicalize ids out of arbitrary URLs.
/// </para>
/// <para>
/// Implementations must be deterministic, allocation-light, and side-effect free
/// — they run on the hot path for every request.
/// </para>
/// </remarks>
public interface IAuditActionResolver
{
    /// <summary>
    /// Resolve the audit descriptor for a matched route, or <see langword="null"/>
    /// when the operation is not in the audit coverage matrix.
    /// </summary>
    /// <param name="httpMethod">The request HTTP method (e.g. <c>GET</c>, <c>POST</c>, <c>DELETE</c>).</param>
    /// <param name="routePattern">
    /// The raw route template of the matched endpoint, or <see langword="null"/>
    /// when no endpoint matched (e.g. a 404 before routing resolved a handler).
    /// </param>
    /// <returns>The descriptor to audit, or <see langword="null"/> to skip.</returns>
    AuditActionDescriptor? Resolve(string httpMethod, string? routePattern);
}
