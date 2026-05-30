// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AzureContainerAppsRevisionDeployBackendTests
{
    [Fact]
    public async Task PlanAsync_BlocksWhenAppNameIsMissing()
    {
        var backend = CreateBackend();

        var plan = await backend.PlanAsync(CreateSpec(
            desiredRevision: "myapp--v2",
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target.resource_id"] = "/subscriptions/sub-123/resourceGroups/rg-prod/providers/Microsoft.App/containerApps/honua-prod",
                ["azure.resource_group"] = "rg-prod"
            }) with
        { TargetName = string.Empty });

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("container app name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PlanAsync_BlocksWhenCanaryWeightSetWithoutTelemetryConnection()
    {
        var backend = CreateBackend();

        var plan = await backend.PlanAsync(CreateSpec(
            desiredRevision: "myapp--v2",
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target.resource_id"] = "/subscriptions/sub-123/resourceGroups/rg-prod/providers/Microsoft.App/containerApps/honua-prod",
                ["azure.resource_group"] = "rg-prod",
                ["containerapp.app_name"] = "honua-prod",
                ["containerapp.canary_weight_percentage"] = "10"
            }));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("telemetry.connection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanAsync_AcceptsValidSpecWithCanaryAndTelemetry()
    {
        var backend = CreateBackend();

        var plan = await backend.PlanAsync(CreateSpec(
            desiredRevision: "myapp--v2",
            parameters: CreateParametersWithCanary()));

        plan.IsReadyToSubmit.Should().BeTrue();
        plan.BlockingReasons.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_SetsCanaryTrafficSplitWhenWeightConfigured()
    {
        var revisionClient = new StubAzureContainerAppsRevisionClient
        {
            TrafficState = new AzureContainerAppsTrafficState
            {
                Traffic =
                [
                    new AzureContainerAppsTrafficWeight { RevisionName = "myapp--v1", Weight = 100 }
                ],
                Revisions =
                [
                    new AzureContainerAppsRevisionSummary { RevisionName = "myapp--v1", Active = true, TrafficWeight = 100 },
                    new AzureContainerAppsRevisionSummary { RevisionName = "myapp--v2", Active = true, TrafficWeight = 0 }
                ]
            }
        };
        var backend = CreateBackend(revisionClient);

        var submission = await backend.StartAsync(CreateOperation(
            desiredRevision: "myapp--v2",
            currentRevision: null,
            parameters: CreateParametersWithCanary()));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ObservedRevision.Should().Be("myapp--v1");
        submission.Message.Should().Contain("10%");
        revisionClient.LastTrafficUpdate.Should().HaveCount(2);
        revisionClient.LastTrafficUpdate![0].RevisionName.Should().Be("myapp--v1");
        revisionClient.LastTrafficUpdate[0].Weight.Should().Be(90);
        revisionClient.LastTrafficUpdate![1].RevisionName.Should().Be("myapp--v2");
        revisionClient.LastTrafficUpdate[1].Weight.Should().Be(10);
    }

    [Fact]
    public async Task StartAsync_CutsOverImmediatelyWhenNoCanaryWeight()
    {
        var revisionClient = new StubAzureContainerAppsRevisionClient
        {
            TrafficState = new AzureContainerAppsTrafficState
            {
                Traffic =
                [
                    new AzureContainerAppsTrafficWeight { RevisionName = "myapp--v1", Weight = 100 }
                ],
                Revisions =
                [
                    new AzureContainerAppsRevisionSummary { RevisionName = "myapp--v1", Active = true, TrafficWeight = 100 }
                ]
            }
        };
        var backend = CreateBackend(revisionClient);

        var submission = await backend.StartAsync(CreateOperation(
            desiredRevision: "myapp--v2",
            currentRevision: null));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ObservedRevision.Should().Be("myapp--v1");
        submission.Message.Should().Contain("100%");
        revisionClient.LastTrafficUpdate.Should().HaveCount(1);
        revisionClient.LastTrafficUpdate![0].RevisionName.Should().Be("myapp--v2");
        revisionClient.LastTrafficUpdate[0].Weight.Should().Be(100);
    }

    [Fact]
    public async Task StartAsync_ActivatesInactiveRevisionBeforeCanarySplit()
    {
        var revisionClient = new StubAzureContainerAppsRevisionClient
        {
            TrafficState = new AzureContainerAppsTrafficState
            {
                Traffic =
                [
                    new AzureContainerAppsTrafficWeight { RevisionName = "myapp--v1", Weight = 100 }
                ],
                Revisions =
                [
                    new AzureContainerAppsRevisionSummary { RevisionName = "myapp--v1", Active = true, TrafficWeight = 100 },
                    new AzureContainerAppsRevisionSummary { RevisionName = "myapp--v2", Active = false, TrafficWeight = 0 }
                ]
            }
        };
        var backend = CreateBackend(revisionClient);

        await backend.StartAsync(CreateOperation(
            desiredRevision: "myapp--v2",
            currentRevision: null,
            parameters: CreateParametersWithCanary()));

        revisionClient.ActivatedRevisions.Should().Contain("myapp--v2");
    }

    [Fact]
    public async Task ObserveAsync_ReturnsSucceededWhenDesiredRevisionHasFullTraffic()
    {
        var revisionClient = new StubAzureContainerAppsRevisionClient
        {
            TrafficState = new AzureContainerAppsTrafficState
            {
                Traffic =
                [
                    new AzureContainerAppsTrafficWeight { RevisionName = "myapp--v2", Weight = 100 }
                ]
            }
        };
        var backend = CreateBackend(revisionClient);

        var observation = await backend.ObserveAsync(CreateOperation(
            desiredRevision: "myapp--v2",
            currentRevision: "myapp--v1",
            status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        observation.ObservedRevision.Should().Be("myapp--v2");
    }

    [Fact]
    public async Task ObserveAsync_ReturnsReconcilingWithPromotionWhenCanarySplitMatchesTarget()
    {
        var revisionClient = new StubAzureContainerAppsRevisionClient
        {
            TrafficState = new AzureContainerAppsTrafficState
            {
                Traffic =
                [
                    new AzureContainerAppsTrafficWeight { RevisionName = "myapp--v1", Weight = 90 },
                    new AzureContainerAppsTrafficWeight { RevisionName = "myapp--v2", Weight = 10 }
                ]
            }
        };
        var backend = CreateBackend(revisionClient);

        var observation = await backend.ObserveAsync(CreateOperation(
            desiredRevision: "myapp--v2",
            currentRevision: "myapp--v1",
            status: WorkflowOperationStatus.Reconciling,
            parameters: CreateParametersWithCanary()));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.PromotionRecommended.Should().BeTrue();
        observation.Message.Should().Contain("10%");
    }

    [Fact]
    public async Task ObserveAsync_ReturnsRolledBackWhenOriginalRevisionRestoredAfterRollback()
    {
        var revisionClient = new StubAzureContainerAppsRevisionClient
        {
            TrafficState = new AzureContainerAppsTrafficState
            {
                Traffic =
                [
                    new AzureContainerAppsTrafficWeight { RevisionName = "myapp--v1", Weight = 100 }
                ]
            }
        };
        var backend = CreateBackend(revisionClient);

        var observation = await backend.ObserveAsync(CreateOperation(
            desiredRevision: "myapp--v2",
            currentRevision: "myapp--v1",
            status: WorkflowOperationStatus.RollbackRequested));

        observation.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        observation.ObservedRevision.Should().Be("myapp--v1");
    }

    [Fact]
    public async Task PromoteAsync_SetsFullTrafficToDesiredRevision()
    {
        var revisionClient = new StubAzureContainerAppsRevisionClient();
        var backend = CreateBackend(revisionClient);

        var observation = await backend.PromoteAsync(CreateOperation(
            desiredRevision: "myapp--v2",
            currentRevision: "myapp--v1",
            status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        observation.ObservedRevision.Should().Be("myapp--v2");
        revisionClient.LastTrafficUpdate.Should().HaveCount(1);
        revisionClient.LastTrafficUpdate![0].RevisionName.Should().Be("myapp--v2");
        revisionClient.LastTrafficUpdate[0].Weight.Should().Be(100);
    }

    [Fact]
    public async Task RollbackAsync_RestoresTrafficToOriginalRevision()
    {
        var revisionClient = new StubAzureContainerAppsRevisionClient();
        var backend = CreateBackend(revisionClient);

        var observation = await backend.RollbackAsync(CreateOperation(
            desiredRevision: "myapp--v2",
            currentRevision: "myapp--v1",
            status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        observation.ObservedRevision.Should().Be("myapp--v1");
        revisionClient.LastTrafficUpdate.Should().HaveCount(1);
        revisionClient.LastTrafficUpdate![0].RevisionName.Should().Be("myapp--v1");
        revisionClient.LastTrafficUpdate[0].Weight.Should().Be(100);
    }

    [Fact]
    public async Task RollbackAsync_ReturnsManualInterventionWhenCurrentRevisionMissing()
    {
        var backend = CreateBackend();

        var observation = await backend.RollbackAsync(CreateOperation(
            desiredRevision: "myapp--v2",
            currentRevision: null,
            status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
    }

    private static AzureContainerAppsRevisionDeployBackend CreateBackend(
        StubAzureContainerAppsRevisionClient? revisionClient = null)
        => new(
            revisionClient ?? new StubAzureContainerAppsRevisionClient(),
            NullLogger<AzureContainerAppsRevisionDeployBackend>.Instance);

    private static DeployOperationSpec CreateSpec(
        string desiredRevision,
        IReadOnlyDictionary<string, string>? parameters = null)
        => new()
        {
            TargetId = "prod-aca",
            TargetKind = DeployTargetKind.AzureContainerApps,
            Backend = "honua-azure-container-apps-revision",
            Environment = "production",
            TargetName = "honua-prod-aca",
            ArtifactReference = "ghcr.io/honua-io/honua-server:latest",
            DesiredRevision = desiredRevision,
            RequiresOutOfBandMigrations = true,
            Parameters = parameters ?? CreateParameters()
        };

    private static WorkflowOperationRecord CreateOperation(
        string desiredRevision,
        string? currentRevision,
        WorkflowOperationStatus status = WorkflowOperationStatus.Submitted,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var spec = CreateSpec(desiredRevision, parameters) with
        {
            CurrentRevision = currentRevision
        };

        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CurrentPhase = "Testing",
            Audit = new OperationAuditInfo(),
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-aca",
                RequiresExclusiveLease = true
            },
            Deploy = spec
        };
    }

    private static Dictionary<string, string> CreateParameters()
        => new(StringComparer.Ordinal)
        {
            ["target.resource_id"] = "/subscriptions/sub-123/resourceGroups/rg-prod/providers/Microsoft.App/containerApps/honua-prod",
            ["azure.resource_group"] = "rg-prod",
            ["containerapp.app_name"] = "honua-prod"
        };

    private static Dictionary<string, string> CreateParametersWithCanary()
        => new(StringComparer.Ordinal)
        {
            ["target.resource_id"] = "/subscriptions/sub-123/resourceGroups/rg-prod/providers/Microsoft.App/containerApps/honua-prod",
            ["azure.resource_group"] = "rg-prod",
            ["containerapp.app_name"] = "honua-prod",
            ["containerapp.canary_weight_percentage"] = "10",
            ["telemetry.connection"] = "prod-prom"
        };

    private sealed class StubAzureContainerAppsRevisionClient : IAzureContainerAppsRevisionClient
    {
        public AzureContainerAppsTrafficState TrafficState { get; set; } = new();

        public IReadOnlyList<AzureContainerAppsTrafficWeight>? LastTrafficUpdate { get; private set; }

        public List<string> ActivatedRevisions { get; } = [];

        public Task<AzureContainerAppsTrafficState> GetTrafficStateAsync(
            string subscriptionId, string resourceGroupName, string appName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(TrafficState);

        public Task<AzureContainerAppsRevisionState> GetRevisionAsync(
            string subscriptionId, string resourceGroupName, string appName,
            string revisionName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AzureContainerAppsRevisionState
            {
                RevisionName = revisionName,
                ProvisioningState = "Provisioned",
                RunningState = "Running",
                Active = true,
                HealthState = "Healthy"
            });

        public Task<AzureContainerAppsTrafficUpdateResult> UpdateTrafficAsync(
            string subscriptionId, string resourceGroupName, string appName,
            IReadOnlyList<AzureContainerAppsTrafficWeight> weights,
            CancellationToken cancellationToken = default)
        {
            LastTrafficUpdate = weights;
            return Task.FromResult(new AzureContainerAppsTrafficUpdateResult
            {
                StatusCode = HttpStatusCode.OK
            });
        }

        public Task ActivateRevisionAsync(
            string subscriptionId, string resourceGroupName, string appName,
            string revisionName, CancellationToken cancellationToken = default)
        {
            ActivatedRevisions.Add(revisionName);
            return Task.CompletedTask;
        }
    }
}
