// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Captures the durable owner snapshot pinned on a custom-code geoprocessing job at
/// submit time (Phase 0 auth spine, Deliverable 1). The snapshot records the
/// submitter's identity, tenant, role snapshot, and the validated declared scope so
/// a later scoped-job callback token can be attenuated to ≤ the submitter.
/// </summary>
/// <remarks>
/// This is intentionally minimal and behavior-preserving: a job that declares no
/// scope (every non-custom-code job today) yields a <see langword="null"/> snapshot
/// and the submit path is unchanged. A job that <em>does</em> declare scope is
/// validated against what the submitter could actually reach — anything the
/// submitter could not reach causes a rejection, so the pinned scope is always
/// ⊆ submitter.
/// </remarks>
internal static class CustomCodeOwnerScopeCapture
{
    /// <summary>
    /// Protocol-metadata key carrying a custom-code job's declared resource scope.
    /// The value is a list of <c>access:service[/layer]</c> entries separated by
    /// <see cref="EntrySeparator"/>, where <c>access</c> is <c>read</c> or
    /// <c>write</c> (e.g. <c>write:parcels/lots;read:zoning</c>).
    /// </summary>
    public const string DeclaredScopeMetadataKey = "honua.customcode.declared_scope";

    /// <summary>Permission claim type the shared RBAC pipeline reads for scoped grants.</summary>
    public const string PermissionClaimType = "permission";

    /// <summary>Tenant claim type mirrored from the portal-token grammar.</summary>
    public const string TenantClaimType = "tenant_id";

    private const char EntrySeparator = ';';
    private const char AccessSeparator = ':';
    private const char LayerSeparator = '/';

    /// <summary>
    /// Builds the owner snapshot for a submission, or returns <see langword="null"/>
    /// when the job declares no custom-code scope (the behavior-preserving default).
    /// </summary>
    /// <param name="principal">The live submitting principal.</param>
    /// <param name="protocolMetadata">The submission's protocol metadata, which may carry a declared scope.</param>
    /// <param name="globalDataEditorRoles">Configured roles that confer write to every service, when known.</param>
    /// <param name="rejection">A human-readable rejection reason when the declared scope exceeds the submitter.</param>
    /// <returns>The pinned snapshot, or <see langword="null"/> when no scope was declared.</returns>
    public static CustomCodeOwnerScope? TryCapture(
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata,
        IReadOnlyList<string>? globalDataEditorRoles,
        out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(principal);
        rejection = null;

        if (protocolMetadata is null ||
            !protocolMetadata.TryGetValue(DeclaredScopeMetadataKey, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var declared = ParseDeclaredScope(raw);
        var roles = SnapshotRoles(principal);
        var grants = SnapshotGrants(principal);

        // Reject anything the submitter could not actually reach at the requested
        // access. The snapshot must be ⊆ submitter; we reject (rather than silently
        // clamp) so a caller asking for more than it has gets a clear signal.
        if (!ScopedJobAttenuation.IsWithinOwner(roles, grants, globalDataEditorRoles, declared, out var unreachable))
        {
            var target = unreachable is null
                ? "(unspecified)"
                : unreachable.LayerId is null
                    ? unreachable.ServiceId
                    : string.Concat(unreachable.ServiceId, "/", unreachable.LayerId);
            rejection =
                $"Declared scope entry '{target}' exceeds the submitter's permissions and cannot be granted to custom code.";
            return null;
        }

        var tenantId = principal.FindFirstValue(TenantClaimType);
        var principalId = principal.Identity?.Name ?? string.Empty;

        // Pin BOTH the role snapshot and the permission-grant snapshot. The mint
        // intersects the requested scope against both, so a submitter whose write
        // authority comes purely from a write:{service}[/{layer}] grant (not a role)
        // gets a correctly-scoped token instead of an under-scoped one.
        return new CustomCodeOwnerScope(principalId, tenantId, roles, grants, declared);
    }

    private static List<JobResourceScopeEntry> ParseDeclaredScope(string raw)
    {
        var entries = new List<JobResourceScopeEntry>();
        foreach (var part in raw.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var accessSep = part.IndexOf(AccessSeparator);
            if (accessSep <= 0 || accessSep >= part.Length - 1)
            {
                // No access prefix: treat as read-only (the access floor).
                AddEntry(entries, JobResourceAccess.Read, part);
                continue;
            }

            var accessToken = part[..accessSep].Trim();
            var target = part[(accessSep + 1)..].Trim();
            var access = string.Equals(accessToken, "write", StringComparison.OrdinalIgnoreCase)
                ? JobResourceAccess.Write
                : JobResourceAccess.Read;
            AddEntry(entries, access, target);
        }

        return entries;
    }

    private static void AddEntry(List<JobResourceScopeEntry> entries, JobResourceAccess access, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var sep = target.IndexOf(LayerSeparator);
        if (sep < 0)
        {
            entries.Add(new JobResourceScopeEntry(target.Trim(), null, access));
            return;
        }

        var service = target[..sep].Trim();
        var layer = target[(sep + 1)..].Trim();
        if (service.Length == 0)
        {
            return;
        }

        entries.Add(new JobResourceScopeEntry(service, layer.Length == 0 ? null : layer, access));
    }

    private static List<string> SnapshotRoles(ClaimsPrincipal principal)
    {
        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        // Also capture the configurable "roles" claim type the RBAC pipeline reads,
        // so a snapshot is faithful regardless of which claim type carried the role.
        foreach (var value in principal.FindAll("roles")
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value) && !roles.Contains(value)))
        {
            roles.Add(value);
        }

        return roles;
    }

    private static List<string> SnapshotGrants(ClaimsPrincipal principal) =>
        principal.FindAll(PermissionClaimType)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
}
