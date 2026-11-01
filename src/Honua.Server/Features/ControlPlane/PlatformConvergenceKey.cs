// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>Builds the shared automatic convergence identity for REST and MCP.</summary>
internal static class PlatformConvergenceKey
{
    internal static async Task<string?> ResolveAsync(
        DeployWorkflowService workflows, string version, string targetId, DeployTargetKind targetKind,
        string? servingIncarnation, WorkflowOperationRecord? lastDeployment, CancellationToken cancellationToken)
    {
        if (targetKind == DeployTargetKind.LocalCompose)
        {
            // The replacement container can appear before the restore child succeeds.
            var owner = await workflows.GetAsync(RestoreDeployAdmission.CellOwnerId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(owner?.CurrentPhase))
            {
                var activeRestore = await workflows.GetAsync(owner.CurrentPhase, cancellationToken).ConfigureAwait(false);
                if (activeRestore?.Kind != WorkflowOperationKind.PreMigrationRestore ||
                    activeRestore.Status is not (WorkflowOperationStatus.Succeeded or WorkflowOperationStatus.Failed))
                    return null;
            }
        }

        string? restoredDeployId = null;
        if (lastDeployment is not null && !string.IsNullOrWhiteSpace(lastDeployment.Deploy?.PreMigrationBackupId))
        {
            var restore = string.IsNullOrWhiteSpace(lastDeployment.Audit.IdempotencyKey)
                ? null
                : await workflows.GetAsync(lastDeployment.Audit.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (restore is not { Kind: WorkflowOperationKind.PreMigrationRestore, Status: WorkflowOperationStatus.Succeeded }
                || !string.Equals(restore.PreMigrationRestore?.DeployOperationId, lastDeployment.OperationId, StringComparison.Ordinal))
                return null;
            restoredDeployId = lastDeployment.OperationId;
        }

        var incarnationSuffix = servingIncarnation is null ? string.Empty : ":" + servingIncarnation;
        var key = $"converge:{version}:{targetId}{incarnationSuffix}";
        return restoredDeployId is null ? key : $"{key}:after:{restoredDeployId}";
    }
}
