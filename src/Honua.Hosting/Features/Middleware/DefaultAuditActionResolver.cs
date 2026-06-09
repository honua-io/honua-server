// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Default, data-driven implementation of <see cref="IAuditActionResolver"/>.
/// Encodes the audit coverage matrix
/// (<c>docs/operator/audit-coverage-matrix.md</c>) so that audit emission stays
/// middleware-driven instead of being scattered across endpoint handlers.
/// </summary>
/// <remarks>
/// <para>
/// Resolution proceeds in two stages so the matrix stays terse:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// Exact, named routes for security-sensitive authentication operations
/// (login, token refresh) are matched against an explicit table keyed by
/// <c>"METHOD routePattern"</c>.
/// </description>
/// </item>
/// <item>
/// <description>
/// Any remaining mutating request (<c>POST</c>/<c>PUT</c>/<c>PATCH</c>/<c>DELETE</c>)
/// whose route falls under the admin control-plane prefix
/// (<c>/api/v{version}/admin</c>) is classified as an administrative action,
/// so the entire admin surface is covered without enumerating every endpoint.
/// </description>
/// </item>
/// </list>
/// <para>
/// Destructive feature writes (single deletes and bulk/transactional edits) are
/// deliberately <i>not</i> resolved here. Because protocols such as WFS-T and
/// gRPC dispatch through a single endpoint where the route cannot reveal the
/// operation, those events are emitted once at the shared edit-pipeline boundary
/// (the <c>IFeatureWriter</c> audit decorator) so every protocol adapter is
/// covered uniformly.
/// </para>
/// </remarks>
internal sealed class DefaultAuditActionResolver : IAuditActionResolver
{
    private const string AdminRoutePrefix = "/api/v{version:apiversion}/admin";

    // Mutating HTTP methods. GET/HEAD/OPTIONS/TRACE never imply a state change and
    // are only audited via the failure path (401/403) handled by the middleware.
    private static readonly FrozenSet<string> MutatingMethods =
        new[] { "POST", "PUT", "PATCH", "DELETE" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Explicit, named routes that need a more specific classification than the
    // admin-prefix fallback — security-sensitive authentication routes.
    private static readonly FrozenDictionary<string, AuditActionDescriptor> NamedRoutes = BuildNamedRoutes();

    public AuditActionDescriptor? Resolve(string httpMethod, string? routePattern)
    {
        if (string.IsNullOrWhiteSpace(httpMethod) || string.IsNullOrWhiteSpace(routePattern))
        {
            return null;
        }

        var normalizedPattern = Normalize(routePattern);
        var key = $"{httpMethod.ToUpperInvariant()} {normalizedPattern}";

        if (NamedRoutes.TryGetValue(key, out var descriptor))
        {
            return descriptor;
        }

        // Admin control-plane fallback: every mutating request under the admin
        // prefix is an administrative action. This keeps "all admin API
        // operations emit audit events" true without per-endpoint wiring.
        if (MutatingMethods.Contains(httpMethod) &&
            normalizedPattern.StartsWith(AdminRoutePrefix, StringComparison.Ordinal))
        {
            return new AuditActionDescriptor
            {
                EventType = AuditEventType.AdminAction,
                Action = $"admin.{httpMethod.ToLowerInvariant()}",
                ResourceType = "admin",
            };
        }

        return null;
    }

    // Route templates can carry route constraints/casing that vary by registration
    // site (e.g. "{version:apiVersion}"). Lower-case + trim trailing slash so the
    // matrix table can use a single canonical form.
    private static string Normalize(string routePattern)
    {
        var trimmed = routePattern.Trim();
        if (trimmed.Length > 1 && trimmed.EndsWith('/'))
        {
            trimmed = trimmed[..^1];
        }

        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.ToLowerInvariant();
    }

    private static FrozenDictionary<string, AuditActionDescriptor> BuildNamedRoutes()
    {
        var authLogin = new AuditActionDescriptor
        {
            EventType = AuditEventType.Authentication,
            Action = "auth.login",
            ResourceType = "session",
        };

        // Token issuance via the OAuth client-credentials / password endpoints is
        // also a login for forensic purposes.
        var authToken = new AuditActionDescriptor
        {
            EventType = AuditEventType.Authentication,
            Action = "auth.token.issue",
            ResourceType = "session",
        };

        var pairs = new Dictionary<string, AuditActionDescriptor>(StringComparer.Ordinal);

        void Add(string method, string pattern, AuditActionDescriptor descriptor)
            => pairs[$"{method} {Normalize(pattern)}"] = descriptor;

        // -- Security-sensitive authentication operations --
        // Admin OIDC backend-assisted login (authorization-code -> token exchange).
        Add("POST", "/api/v{version:apiVersion}/admin/auth/providers/{providerKey}/token", authLogin);

        // First-party OAuth token endpoints (api-key / client-credentials issuance).
        Add("POST", "/oauth/token", authToken);
        Add("POST", "/sharing/rest/oauth2/token", authToken);

        return pairs.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
