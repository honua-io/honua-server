// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// The access level a <see cref="JobResourceScopeEntry"/> grants over its target
/// resource. Read is the floor: a <see cref="JobResourceAccess.Write"/> entry also
/// confers read, while a <see cref="JobResourceAccess.Read"/> entry never confers
/// write (custom-code auth spine invariant #3 — read-only unless write declared).
/// </summary>
public enum JobResourceAccess
{
    /// <summary>
    /// Read-only access (query/metadata) to the scoped resource. This is the
    /// default when an entry does not explicitly declare write.
    /// </summary>
    Read = 0,

    /// <summary>
    /// Read and write (insert/update/delete) access to the scoped resource.
    /// </summary>
    Write = 1,
}

/// <summary>
/// A single resource-scope grant for a custom-code geoprocessing job: the
/// <c>(serviceId, layerId?, access)</c> tuple identifying one service (or one
/// layer within it) the job's SDK callbacks may reach, and at what access level.
/// </summary>
/// <remarks>
/// A null <see cref="LayerId"/> denotes a service-wide entry that covers every
/// layer of the named service; a non-null <see cref="LayerId"/> narrows the entry
/// to that single layer. This mirrors the existing layer-scoped write-key grant
/// grammar (<c>write:{service}</c> vs <c>write:{service}/{layer}</c>) so the scope
/// flows through the same shared RBAC pipeline rather than a parallel ACL model.
/// </remarks>
/// <param name="ServiceId">The target service identifier (required).</param>
/// <param name="LayerId">The target layer identifier, or <see langword="null"/> for a service-wide entry.</param>
/// <param name="Access">The access level granted over the target.</param>
public sealed record JobResourceScopeEntry(
    string ServiceId,
    string? LayerId,
    JobResourceAccess Access);

/// <summary>
/// The durable owner snapshot pinned at submit time for a custom-code
/// geoprocessing job. It captures exactly the authorization facts the scoped-job
/// token issuer needs to attenuate a callback token to <em>≤ the submitter's</em>
/// permissions: the submitter's principal id, tenant, role snapshot, and the
/// (already validated, ⊆ submitter) scope the job declared it needs.
/// </summary>
/// <remarks>
/// This snapshot is the <em>sole</em> source of truth the mint reads — nothing
/// user-supplied at callback time can widen it. <see cref="TenantId"/> in
/// particular is captured here from the live principal's <c>tenant_id</c> claim
/// and is never re-derived from a caller-supplied value, so cross-tenant access
/// is structurally impossible (invariant #2).
/// </remarks>
/// <param name="PrincipalId">Stable identifier of the submitting principal.</param>
/// <param name="TenantId">Tenant the submitter belonged to, or <see langword="null"/> for the tenant-less default.</param>
/// <param name="Roles">Snapshot of the submitter's roles at submit time.</param>
/// <param name="Grants">
/// Snapshot of the submitter's per-resource <c>read:</c>/<c>write:{service}[/{layer}]</c>
/// permission grants at submit time. These carry the write authority a submitter
/// holds purely via a permission grant rather than a role; capturing them lets the
/// mint intersect the requested scope against BOTH the owner's roles and grants, so a
/// grant-based submitter's custom-code job is no longer under-scoped. Empty/absent
/// when the submitter carries no fine-grained grants.
/// </param>
/// <param name="DeclaredScope">
/// The resource scope the job declared it needs, already clamped to ⊆ what the
/// submitter could reach at submit time. Empty when the job declared no scope
/// (the behavior-preserving default for non-custom-code jobs).
/// </param>
public sealed record CustomCodeOwnerScope(
    string PrincipalId,
    string? TenantId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Grants,
    IReadOnlyList<JobResourceScopeEntry> DeclaredScope);
