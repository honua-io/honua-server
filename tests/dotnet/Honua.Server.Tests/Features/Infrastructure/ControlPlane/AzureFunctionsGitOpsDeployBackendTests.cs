// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AzureFunctionsGitOpsDeployBackendTests
{
    [Fact]
    public async Task PlanAsync_BlocksWhenImageMetadataIsMissing()
    {
        var backend = new AzureFunctionsGitOpsDeployBackend(
            new StubAzureFunctionsSlotClient(),
            NullLogger<AzureFunctionsGitOpsDeployBackend>.Instance);

        var plan = await backend.PlanAsync(CreateSpec(
            desiredRevision: "staging",
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target.resource_id"] = "/subscriptions/sub-123/resourceGroups/rg-prod/providers/Microsoft.Web/sites/honua-prod-functions",
                ["azure.resource_group"] = "rg-prod",
                ["functions.app_name"] = "honua-prod-functions",
                ["functions.slot_name"] = "staging"
            }));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("functions.current_image", StringComparison.Ordinal));
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("functions.desired_image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_SwapsStagingSlotWithProductionWhenImagesMatchExpectedTopology()
    {
        var slotClient = new StubAzureFunctionsSlotClient
        {
            ProductionState = new AzureFunctionsSiteConfigState
            {
                LinuxFxVersion = "DOCKER|ghcr.io/honua-io/honua-server:old"
            },
            SlotState = new AzureFunctionsSiteConfigState
            {
                LinuxFxVersion = "DOCKER|ghcr.io/honua-io/honua-server:new"
            }
        };
        var backend = new AzureFunctionsGitOpsDeployBackend(slotClient, NullLogger<AzureFunctionsGitOpsDeployBackend>.Instance);

        var submission = await backend.StartAsync(CreateOperation("staging", "production"));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ObservedRevision.Should().Be("ghcr.io/honua-io/honua-server:old");
        slotClient.LastSwapRequest.Should().NotBeNull();
        slotClient.LastSwapRequest!.SlotName.Should().Be("staging");
        slotClient.LastSwapRequest.PreserveVnet.Should().BeTrue();
    }

    [Fact]
    public async Task ObserveAsync_ReturnsSucceededWhenProductionMatchesDesiredImage()
    {
        var slotClient = new StubAzureFunctionsSlotClient
        {
            ProductionState = new AzureFunctionsSiteConfigState
            {
                LinuxFxVersion = "DOCKER|ghcr.io/honua-io/honua-server:new"
            },
            SlotState = new AzureFunctionsSiteConfigState
            {
                LinuxFxVersion = "DOCKER|ghcr.io/honua-io/honua-server:old"
            }
        };
        var backend = new AzureFunctionsGitOpsDeployBackend(slotClient, NullLogger<AzureFunctionsGitOpsDeployBackend>.Instance);

        var observation = await backend.ObserveAsync(CreateOperation("staging", "production", WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        observation.ObservedRevision.Should().Be("ghcr.io/honua-io/honua-server:new");
    }

    [Fact]
    public async Task RollbackAsync_SwapsStagingSlotBackToProduction()
    {
        var slotClient = new StubAzureFunctionsSlotClient
        {
            ProductionState = new AzureFunctionsSiteConfigState
            {
                LinuxFxVersion = "DOCKER|ghcr.io/honua-io/honua-server:new"
            },
            SlotState = new AzureFunctionsSiteConfigState
            {
                LinuxFxVersion = "DOCKER|ghcr.io/honua-io/honua-server:old"
            }
        };
        var backend = new AzureFunctionsGitOpsDeployBackend(slotClient, NullLogger<AzureFunctionsGitOpsDeployBackend>.Instance);

        var observation = await backend.RollbackAsync(CreateOperation("staging", "production", WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        observation.ObservedRevision.Should().Be("ghcr.io/honua-io/honua-server:new");
        slotClient.LastSwapRequest.Should().NotBeNull();
        slotClient.LastSwapRequest!.SlotName.Should().Be("staging");
    }

    private static DeployOperationSpec CreateSpec(
        string desiredRevision,
        IReadOnlyDictionary<string, string>? parameters = null)
        => new()
        {
            TargetId = "prod-functions",
            TargetKind = DeployTargetKind.AzureFunctions,
            Backend = "honua-gitops-azure-functions",
            Environment = "production",
            TargetName = "honua-prod-functions",
            ArtifactReference = "ghcr.io/honua-io/honua-server:new",
            DesiredRevision = desiredRevision,
            RequiresOutOfBandMigrations = true,
            Parameters = parameters ?? CreateParameters()
        };

    private static WorkflowOperationRecord CreateOperation(
        string desiredRevision,
        string? currentRevision,
        WorkflowOperationStatus status = WorkflowOperationStatus.Submitted)
    {
        var spec = CreateSpec(desiredRevision) with
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
                PartitionKey = "production:prod-functions",
                RequiresExclusiveLease = true
            },
            Deploy = spec
        };
    }

    private static Dictionary<string, string> CreateParameters()
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["target.resource_id"] = "/subscriptions/sub-123/resourceGroups/rg-prod/providers/Microsoft.Web/sites/honua-prod-functions",
            ["azure.resource_group"] = "rg-prod",
            ["functions.app_name"] = "honua-prod-functions",
            ["functions.slot_name"] = "staging",
            ["functions.current_image"] = "ghcr.io/honua-io/honua-server:old",
            ["functions.desired_image"] = "ghcr.io/honua-io/honua-server:new"
        };

    private sealed class StubAzureFunctionsSlotClient : IAzureFunctionsSlotClient
    {
        public AzureFunctionsSiteConfigState ProductionState { get; set; } = new()
        {
            LinuxFxVersion = "DOCKER|ghcr.io/honua-io/honua-server:old"
        };

        public AzureFunctionsSiteConfigState SlotState { get; set; } = new()
        {
            LinuxFxVersion = "DOCKER|ghcr.io/honua-io/honua-server:new"
        };

        public SwapRequest? LastSwapRequest { get; private set; }

        public Task<AzureFunctionsSiteConfigState> GetSiteConfigAsync(
            string subscriptionId,
            string resourceGroupName,
            string functionAppName,
            string? slotName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(string.IsNullOrWhiteSpace(slotName) ? ProductionState : SlotState);

        public Task<AzureFunctionsSlotSwapResult> SwapSlotWithProductionAsync(
            string subscriptionId,
            string resourceGroupName,
            string functionAppName,
            string slotName,
            bool preserveVnet,
            CancellationToken cancellationToken = default)
        {
            LastSwapRequest = new SwapRequest(subscriptionId, resourceGroupName, functionAppName, slotName, preserveVnet);
            return Task.FromResult(new AzureFunctionsSlotSwapResult
            {
                StatusCode = HttpStatusCode.OK,
                OperationLocation = $"https://management.azure.com{functionAppName}/{slotName}"
            });
        }
    }

    private sealed record SwapRequest(
        string SubscriptionId,
        string ResourceGroupName,
        string FunctionAppName,
        string SlotName,
        bool PreserveVnet);
}
