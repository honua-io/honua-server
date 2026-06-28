// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Export;

namespace Honua.Core.Features.AuditLog;

/// <summary>
/// No-op <see cref="IAuditRetentionPruner"/> used when no durable audit store is
/// configured (tests and non-database hosts). Prunes nothing and reports zero.
/// </summary>
/// <remarks>
/// This is intentionally <c>sealed</c> and stateless so DI can hand out a
/// singleton without per-call allocation, mirroring <see cref="NullAuditLog"/>.
/// A host with a real audit store registers a provider-backed pruner (for
/// example the Postgres implementation) ahead of this fallback.
/// </remarks>
public sealed class NullAuditRetentionPruner : IAuditRetentionPruner
{
    /// <summary>A shared, allocation-free instance.</summary>
    public static readonly NullAuditRetentionPruner Instance = new();

    /// <inheritdoc />
    public Task<int> PruneAsync(AuditRetentionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Task.FromResult(0);
    }
}
