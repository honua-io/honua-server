// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Service-bound maintenance. Implementations validate the persisted canonical service identity
/// inside the same maintenance lock as reconcile/post, including when a queued job starts later.
/// Unscoped IVersionManager methods remain available to trusted store administration.
/// </summary>
public interface IVersionServiceMaintenanceManager
{
    /// <summary>Reconciles only a branch associated with the specified canonical service.</summary>
    Task<VersionReconcileResult> ReconcileForServiceAsync(
        string serviceId, Guid versionId, VersionReconcilePolicy policy = VersionReconcilePolicy.None,
        VersionConflictDetection detection = VersionConflictDetection.ByAttribute,
        CancellationToken cancellationToken = default);

    /// <summary>Posts only a branch associated with the specified canonical service.</summary>
    Task<VersionPostResult> PostForServiceAsync(
        string serviceId, Guid versionId, CancellationToken cancellationToken = default);
}
