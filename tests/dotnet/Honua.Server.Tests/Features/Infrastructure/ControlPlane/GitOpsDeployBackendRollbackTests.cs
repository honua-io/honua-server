// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Fail-closed rollback coverage for the built-in GitOps hand-off backend (#2811). The default GitOps
/// backend hands desired state off to an external controller and holds no revert credentials, so it
/// cannot execute an automated rollback. The previous implementation logged a line and returned
/// <see cref="WorkflowOperationStatus.RollbackRequested"/> (a false "in progress" signal that then
/// silently parked as <see cref="WorkflowOperationStatus.ManualInterventionRequired"/>); it must now
/// fail loudly with a terminal, actionable manual-intervention observation instead.
/// </summary>
public sealed class GitOpsDeployBackendRollbackTests
{
    [Fact]
    public async Task RollbackAsync_WithKnownGoodRevision_FailsLoudly_WithActionableManualIntervention()
    {
        var backend = new KubernetesGitOpsDeployBackend(NullLogger<KubernetesGitOpsDeployBackend>.Instance);
        var operation = CreateOperation(currentRevision: "sha256:old");

        var observation = await backend.RollbackAsync(operation);

        observation.Status.Should().Be(
            WorkflowOperationStatus.ManualInterventionRequired,
            "the built-in GitOps backend cannot revert automatically and must not fake progress");
        observation.Status.Should().NotBe(WorkflowOperationStatus.RollbackRequested);
        observation.Message.Should().Contain("cannot perform an automated rollback");
        observation.Message.Should().Contain("revert the desired-state manifest");
        observation.Message.Should().Contain("sha256:old");
        observation.ObservedRevision.Should().Be("sha256:old");
    }

    [Fact]
    public async Task RollbackAsync_WithoutKnownGoodRevision_NamesTheMissingRollbackTarget()
    {
        var backend = new KubernetesGitOpsDeployBackend(NullLogger<KubernetesGitOpsDeployBackend>.Instance);
        var operation = CreateOperation(currentRevision: null);

        var observation = await backend.RollbackAsync(operation);

        observation.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        observation.Message.Should().Contain("no previously-recorded known-good revision");
    }

    [Fact]
    public async Task RollbackAsync_WhenReleaseContractedSchema_WarnsAgainstStrandingTheBinary()
    {
        var backend = new KubernetesGitOpsDeployBackend(NullLogger<KubernetesGitOpsDeployBackend>.Instance);
        var operation = CreateOperation(currentRevision: "sha256:old", requiresOutOfBandMigrations: true);

        var observation = await backend.RollbackAsync(operation);

        observation.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        observation.Message.Should().Contain("contracted schema");
    }

    private static WorkflowOperationRecord CreateOperation(
        string? currentRevision,
        bool requiresOutOfBandMigrations = false)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.RollbackRequested,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Rollback requested",
            Audit = new OperationAuditInfo(),
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-api",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "prod-api",
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                CurrentRevision = currentRevision,
                DesiredRevision = "sha256:new",
                RequiresOutOfBandMigrations = requiresOutOfBandMigrations,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            }
        };
    }
}
