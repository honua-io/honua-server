// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class KubernetesArgoRolloutsDeployBackendTests
{
    private const string Namespace = "honua-prod";
    private const string RolloutName = "honua-server";
    private const string ContainerName = "honua";
    private const string DesiredImage = "ghcr.io/honua/honua-server:sha-42";
    private const string PreviousImage = "ghcr.io/honua/honua-server:sha-41";

    [Fact]
    public async Task PlanAsync_MissingRolloutName_HasBlockingReason()
    {
        var backend = CreateBackend();

        var parameters = BaseParameters();
        parameters.Remove(KubernetesArgoRolloutsDeployBackend.RolloutNameParameter);

        var plan = await backend.PlanAsync(CreateSpec(parameters: parameters, targetName: ""));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("rollout_name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanAsync_MissingContainerName_HasBlockingReason()
    {
        var backend = CreateBackend();

        var parameters = BaseParameters();
        parameters.Remove(KubernetesArgoRolloutsDeployBackend.ContainerNameParameter);

        var plan = await backend.PlanAsync(CreateSpec(parameters: parameters));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("container_name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanAsync_CanaryWeightWithoutTelemetryConnection_HasBlockingReason()
    {
        var backend = CreateBackend();

        var parameters = BaseParameters();
        parameters["deployment.canary_weight_percentage"] = "25";

        var plan = await backend.PlanAsync(CreateSpec(parameters: parameters));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("telemetry.connection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanAsync_CanaryWeightOutOfRange_HasBlockingReason()
    {
        var backend = CreateBackend();

        var parameters = BaseParameters();
        parameters["deployment.canary_weight_percentage"] = "150";
        parameters["telemetry.connection"] = "prod-prom";

        var plan = await backend.PlanAsync(CreateSpec(parameters: parameters));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("canary weight", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PlanAsync_ValidImmediateParameters_IsReadyToSubmit()
    {
        var backend = CreateBackend();

        var plan = await backend.PlanAsync(CreateSpec());

        plan.IsReadyToSubmit.Should().BeTrue();
        plan.BlockingReasons.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_ValidCanaryParameters_IsReadyToSubmit()
    {
        var backend = CreateBackend();

        var plan = await backend.PlanAsync(CreateSpec(parameters: CanaryParameters()));

        plan.IsReadyToSubmit.Should().BeTrue();
        plan.BlockingReasons.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_SetsImageAndCapturesPreviousImage()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = ProgressingRollout(PreviousImage)
        };
        var backend = CreateBackend(client);

        var submission = await backend.StartAsync(CreateOperation(parameters: CanaryParameters()));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ProviderOperationId.Should().Be($"{Namespace}/{RolloutName}");
        submission.ObservedRevision.Should().Be(PreviousImage);
        client.LastSetImage.Should().Be(DesiredImage);
        client.LastSetImageContainer.Should().Be(ContainerName);
    }

    [Fact]
    public async Task StartAsync_RolloutMissing_ReturnsFailed()
    {
        var client = new StubArgoRolloutsClient { RolloutState = null };
        var backend = CreateBackend(client);

        var submission = await backend.StartAsync(CreateOperation(parameters: CanaryParameters()));

        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        submission.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task ObserveAsync_PausedAtExpectedCanaryWeight_RecommendsPromotion()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = new ArgoRolloutState
            {
                Name = RolloutName,
                Phase = ArgoRolloutPhase.Paused,
                IsPaused = true,
                CanaryWeight = 25,
                PodTemplateImage = DesiredImage,
                CurrentPodHash = "abc123",
                StableRevisionHash = "stable999"
            }
        };
        var backend = CreateBackend(client);

        var observation = await backend.ObserveAsync(CreateOperation(
            parameters: CanaryParameters(),
            status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.PromotionRecommended.Should().BeTrue();
        observation.RollbackRecommended.Should().BeFalse();
        observation.ObservedRevision.Should().Be(DesiredImage);
    }

    [Fact]
    public async Task ObserveAsync_PausedAtWrongCanaryWeight_DoesNotRecommendPromotion()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = new ArgoRolloutState
            {
                Name = RolloutName,
                Phase = ArgoRolloutPhase.Paused,
                IsPaused = true,
                CanaryWeight = 5,
                PodTemplateImage = DesiredImage
            }
        };
        var backend = CreateBackend(client);

        var observation = await backend.ObserveAsync(CreateOperation(
            parameters: CanaryParameters(),
            status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.PromotionRecommended.Should().BeFalse();
    }

    [Fact]
    public async Task ObserveAsync_HealthyAndStable_ReturnsSucceeded()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = new ArgoRolloutState
            {
                Name = RolloutName,
                Phase = ArgoRolloutPhase.Healthy,
                PodTemplateImage = DesiredImage,
                CurrentPodHash = "abc123",
                StableRevisionHash = "abc123"
            }
        };
        var backend = CreateBackend(client);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        observation.ObservedRevision.Should().Be(DesiredImage);
    }

    [Fact]
    public async Task ObserveAsync_HealthyButNotYetStable_StaysReconciling()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = new ArgoRolloutState
            {
                Name = RolloutName,
                Phase = ArgoRolloutPhase.Healthy,
                PodTemplateImage = DesiredImage,
                CurrentPodHash = "abc123",
                StableRevisionHash = "stable999"
            }
        };
        var backend = CreateBackend(client);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
    }

    [Fact]
    public async Task ObserveAsync_DegradedRollout_RecommendsRollback()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = new ArgoRolloutState
            {
                Name = RolloutName,
                Phase = ArgoRolloutPhase.Degraded,
                Message = "canary analysis failed",
                PodTemplateImage = DesiredImage
            }
        };
        var backend = CreateBackend(client);

        var observation = await backend.ObserveAsync(CreateOperation(
            parameters: CanaryParameters(),
            status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.RollbackRecommended.Should().BeTrue();
        observation.Message.Should().Contain("degraded");
    }

    [Fact]
    public async Task ObserveAsync_AbortedRollout_RecommendsRollback()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = new ArgoRolloutState
            {
                Name = RolloutName,
                Phase = ArgoRolloutPhase.Progressing,
                IsAborted = true,
                PodTemplateImage = DesiredImage
            }
        };
        var backend = CreateBackend(client);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.RollbackRecommended.Should().BeTrue();
    }

    [Fact]
    public async Task ObserveAsync_RollbackRequested_RevertedToStable_ReturnsRolledBack()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = new ArgoRolloutState
            {
                Name = RolloutName,
                Phase = ArgoRolloutPhase.Healthy,
                PodTemplateImage = PreviousImage,
                CurrentPodHash = "stable999",
                StableRevisionHash = "stable999"
            }
        };
        var backend = CreateBackend(client);

        var observation = await backend.ObserveAsync(CreateOperation(
            currentRevision: PreviousImage,
            status: WorkflowOperationStatus.RollbackRequested));

        observation.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        observation.ObservedRevision.Should().Be(PreviousImage);
    }

    [Fact]
    public async Task ObserveAsync_RollbackRequested_StillSettling_RemainsRollbackRequested()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = new ArgoRolloutState
            {
                Name = RolloutName,
                Phase = ArgoRolloutPhase.Progressing,
                IsAborted = true,
                PodTemplateImage = DesiredImage,
                CurrentPodHash = "abc123",
                StableRevisionHash = "stable999"
            }
        };
        var backend = CreateBackend(client);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.RollbackRequested));

        observation.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
    }

    [Fact]
    public async Task ObserveAsync_RolloutMissing_ReturnsFailed()
    {
        var client = new StubArgoRolloutsClient { RolloutState = null };
        var backend = CreateBackend(client);

        var observation = await backend.ObserveAsync(CreateOperation());

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task PromoteAsync_AdvancesRollout_StaysReconcilingUntilHealthy()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = new ArgoRolloutState
            {
                Name = RolloutName,
                Phase = ArgoRolloutPhase.Progressing,
                PodTemplateImage = DesiredImage,
                CurrentPodHash = "abc123",
                StableRevisionHash = "stable999"
            }
        };
        var backend = CreateBackend(client);

        var observation = await backend.PromoteAsync(CreateOperation(parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        client.PromoteCalled.Should().BeTrue();
    }

    [Fact]
    public async Task PromoteAsync_RolloutHealthyAndStable_ReturnsSucceeded()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = new ArgoRolloutState
            {
                Name = RolloutName,
                Phase = ArgoRolloutPhase.Healthy,
                PodTemplateImage = DesiredImage,
                CurrentPodHash = "abc123",
                StableRevisionHash = "abc123"
            }
        };
        var backend = CreateBackend(client);

        var observation = await backend.PromoteAsync(CreateOperation(parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        client.PromoteCalled.Should().BeTrue();
    }

    [Fact]
    public async Task RollbackAsync_AbortsRollout_ReturnsRollbackRequested()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = ProgressingRollout(DesiredImage)
        };
        var backend = CreateBackend(client);

        var observation = await backend.RollbackAsync(CreateOperation(
            currentRevision: PreviousImage,
            parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        observation.ObservedRevision.Should().Be(PreviousImage);
        client.AbortCalled.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_HttpError_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var client = new StubArgoRolloutsClient
        {
            RolloutState = ProgressingRollout(PreviousImage),
            SetImageException = new HttpRequestException("Forbidden: rollout token=secret-token-abc", null, HttpStatusCode.Forbidden)
        };
        var backend = CreateBackend(client);

        var submission = await backend.StartAsync(CreateOperation(parameters: CanaryParameters()));

        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        submission.Message.Should().NotBeNullOrEmpty();
        submission.Message.Should().NotContain("secret-token-abc");
    }

    [Fact]
    public async Task ObserveAsync_HttpError_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var client = new StubArgoRolloutsClient
        {
            GetException = new HttpRequestException("Unauthorized: bearer=secret-bearer-xyz", null, HttpStatusCode.Unauthorized)
        };
        var backend = CreateBackend(client);

        var observation = await backend.ObserveAsync(CreateOperation());

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().NotContain("secret-bearer-xyz");
    }

    [Fact]
    public async Task RollbackAsync_HttpError_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var client = new StubArgoRolloutsClient
        {
            AbortException = new HttpRequestException("Conflict: resourceVersion=secret-rv-999", null, HttpStatusCode.Conflict)
        };
        var backend = CreateBackend(client);

        var observation = await backend.RollbackAsync(CreateOperation(parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().NotContain("secret-rv-999");
    }

    [Fact]
    public async Task GetCapabilitiesAsync_AdvertisesTrafficShiftingAndPromotion()
    {
        var backend = CreateBackend();

        var capabilities = await backend.GetCapabilitiesAsync();

        capabilities.SupportsRollback.Should().BeTrue();
        capabilities.SupportsTrafficShifting.Should().BeTrue();
        capabilities.SupportsProgressPolling.Should().BeTrue();
        capabilities.SupportsRevisionPinning.Should().BeTrue();
        capabilities.SupportsCancellation.Should().BeFalse();
    }

    [Fact]
    public void BackendName_AndTargetKind_AreStable()
    {
        var backend = CreateBackend();

        backend.BackendName.Should().Be("honua-kubernetes-argo-rollouts");
        backend.TargetKind.Should().Be(DeployTargetKind.Kubernetes);
    }

    private static KubernetesArgoRolloutsDeployBackend CreateBackend(StubArgoRolloutsClient? client = null)
        => new(
            client ?? new StubArgoRolloutsClient { RolloutState = ProgressingRollout(DesiredImage) },
            NullLogger<KubernetesArgoRolloutsDeployBackend>.Instance);

    private static ArgoRolloutState ProgressingRollout(string image)
        => new()
        {
            Name = RolloutName,
            Phase = ArgoRolloutPhase.Progressing,
            PodTemplateImage = image,
            CurrentPodHash = "abc123",
            StableRevisionHash = "stable999"
        };

    private static DeployOperationSpec CreateSpec(
        string desiredRevision = DesiredImage,
        string targetName = RolloutName,
        IReadOnlyDictionary<string, string>? parameters = null)
        => new()
        {
            TargetId = "prod-k8s",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "honua-kubernetes-argo-rollouts",
            Environment = "production",
            TargetName = targetName,
            ArtifactReference = "ghcr.io/honua/honua-server",
            DesiredRevision = desiredRevision,
            RequiresOutOfBandMigrations = true,
            Parameters = parameters ?? BaseParameters()
        };

    private static WorkflowOperationRecord CreateOperation(
        string desiredRevision = DesiredImage,
        string? currentRevision = null,
        WorkflowOperationStatus status = WorkflowOperationStatus.Submitted,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var spec = CreateSpec(desiredRevision, parameters: parameters) with
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
                PartitionKey = "production:prod-k8s",
                RequiresExclusiveLease = true
            },
            Deploy = spec
        };
    }

    private static Dictionary<string, string> BaseParameters()
        => new(StringComparer.Ordinal)
        {
            [KubernetesArgoRolloutsDeployBackend.NamespaceParameter] = Namespace,
            [KubernetesArgoRolloutsDeployBackend.RolloutNameParameter] = RolloutName,
            [KubernetesArgoRolloutsDeployBackend.ContainerNameParameter] = ContainerName
        };

    private static Dictionary<string, string> CanaryParameters()
    {
        var parameters = BaseParameters();
        parameters["deployment.canary_weight_percentage"] = "25";
        parameters["telemetry.connection"] = "prod-prom";
        return parameters;
    }

    private sealed class StubArgoRolloutsClient : IArgoRolloutsClient
    {
        public ArgoRolloutState? RolloutState { get; set; }

        public string? LastSetImage { get; private set; }

        public string? LastSetImageContainer { get; private set; }

        public bool PromoteCalled { get; private set; }

        public bool AbortCalled { get; private set; }

        public Exception? GetException { get; set; }

        public Exception? SetImageException { get; set; }

        public Exception? PromoteException { get; set; }

        public Exception? AbortException { get; set; }

        public Task<ArgoRolloutState?> GetRolloutAsync(string @namespace, string name, CancellationToken cancellationToken = default)
        {
            if (GetException != null)
            {
                throw GetException;
            }

            return Task.FromResult(RolloutState);
        }

        public Task<ArgoRolloutState> SetImageAsync(string @namespace, string name, string containerName, string image, CancellationToken cancellationToken = default)
        {
            if (SetImageException != null)
            {
                throw SetImageException;
            }

            LastSetImage = image;
            LastSetImageContainer = containerName;
            return Task.FromResult(RolloutState ?? throw new InvalidOperationException("RolloutState not configured."));
        }

        public Task<ArgoRolloutState> PromoteAsync(string @namespace, string name, CancellationToken cancellationToken = default)
        {
            if (PromoteException != null)
            {
                throw PromoteException;
            }

            PromoteCalled = true;
            return Task.FromResult(RolloutState ?? throw new InvalidOperationException("RolloutState not configured."));
        }

        public Task<ArgoRolloutState> AbortAsync(string @namespace, string name, CancellationToken cancellationToken = default)
        {
            if (AbortException != null)
            {
                throw AbortException;
            }

            AbortCalled = true;
            return Task.FromResult(RolloutState ?? throw new InvalidOperationException("RolloutState not configured."));
        }
    }
}
