// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Real-AWS certification lane (#2164) — failure-of-failure cell for #2161. It drives the
/// <see cref="DeployWorkflowReconciler"/> through a telemetry-triggered rollback using the production
/// <see cref="AwsEcsAlbDeployBackend"/> and real AWS SDK clients, then injects a live ELBv2
/// rollback failure by substituting a non-existent listener-rule ARN only for the rollback call.
///
/// SAFETY: the observe phase reads the real standing ECS/ALB cert substrate, but the failing rollback
/// targets a syntactically valid rule ARN that does not exist. <see cref="AwsSdkAlbClient"/> describes
/// the rule before <c>ModifyRule</c>, so AWS returns the failure before any traffic weight mutation.
/// The test asserts the real listener rule remains at the known stable=100/canary=0 baseline after
/// the reconciler escalates to <see cref="WorkflowOperationStatus.ManualInterventionRequired"/>.
/// OFF unless <see cref="RealAwsCertificationFixture.EcsAlbConfigured"/>; skips otherwise.
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.RealAwsCertification)]
public sealed class AwsEcsAlbRollbackFailureRealCertificationTests : IClassFixture<RealAwsCertificationFixture>
{
    private readonly RealAwsCertificationFixture _cert;
    private readonly ITestOutputHelper _output;

    public AwsEcsAlbRollbackFailureRealCertificationTests(RealAwsCertificationFixture cert, ITestOutputHelper output)
    {
        _cert = cert;
        _output = output;
    }

    [SkippableFact]
    public async Task RollbackFailure_EscalatesToManualIntervention_ThroughProductionBackend()
    {
        Skip.IfNot(
            _cert.EcsAlbConfigured,
            "Real-AWS ECS/ALB failure-of-failure cell not configured (needs HONUA_REALAWS_CERT_ENABLED=true, "
            + "HONUA_REALAWS_CERT_ECS_CLUSTER, HONUA_REALAWS_CERT_ECS_SERVICE, "
            + "HONUA_REALAWS_CERT_ALB_LISTENER_ARN, HONUA_REALAWS_CERT_CANARY_TARGET_GROUP_ARN, "
            + "HONUA_REALAWS_CERT_STABLE_TARGET_GROUP_ARN with credentials present).");

        var region = _cert.Region;
        var cluster = _cert.EcsCluster!;
        var service = _cert.EcsService!;
        var canaryTargetGroup = _cert.CanaryTargetGroupArn!;
        var stableTargetGroup = _cert.StableTargetGroupArn!;

        using var albClient = new AwsSdkAlbClient();
        using var ecsClient = new AwsSdkEcsClient();

        var listenerRuleArn = await AwsEcsAlbCertificationSupport.ResolveWeightedRuleArnAsync(
            _cert.AlbListenerArn!, canaryTargetGroup, stableTargetGroup, region);

        var startingShares = AwsEcsAlbCertificationSupport.ReadShares(
            await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region),
            canaryTargetGroup,
            stableTargetGroup);
        startingShares.Stable.Should().Be(
            AwsEcsAlbCertificationSupport.BaselineStableWeight,
            "the rollback-failure cert must start from the same known baseline as the happy-path ECS/ALB cell");
        startingShares.Canary.Should().Be(
            AwsEcsAlbCertificationSupport.BaselineCanaryWeight,
            "the rollback-failure cert must not run against an already-shifted substrate");

        var currentService = await ecsClient.DescribeServiceAsync(cluster, service, region);
        var currentTaskDefinition = currentService.TaskDefinitionArn;
        currentTaskDefinition.Should().NotBeNullOrWhiteSpace(
            "the certification ECS service must resolve to a concrete task definition for observation");

        var operation = AwsEcsAlbCertificationSupport.BuildOperation(
            cluster,
            service,
            listenerRuleArn,
            canaryTargetGroup,
            stableTargetGroup,
            region,
            currentTaskDefinition!,
            operationPrefix: "cert-ecs-alb-rollback-failure") with
        {
            Status = WorkflowOperationStatus.Reconciling,
            CurrentPhase = "Certification rollback fault injection",
        };

        var missingRuleArn = BuildMissingRuleArn(listenerRuleArn);
        _output.WriteLine($"[cert] injecting rollback failure with non-existent listener rule '{missingRuleArn}'.");

        var realBackend = new AwsEcsAlbDeployBackend(
            albClient,
            ecsClient,
            NullLogger<AwsEcsAlbDeployBackend>.Instance);
        var backend = new RollbackRuleFaultInjectingBackend(realBackend, missingRuleArn);
        var store = new InMemoryWorkflowOperationStore();
        var targetRegistry = new SingleTargetRegistry(operation.Deploy!);
        var telemetry = new StubDeployTelemetrySignalEvaluator(new DeployTelemetryDecision
        {
            RollbackRecommended = true,
            Message = "Certification telemetry fault requested rollback."
        });
        var reconciler = new DeployWorkflowReconciler(
            store,
            targetRegistry,
            [backend],
            telemetry,
            NullLogger<DeployWorkflowReconciler>.Instance);

        await store.TryCreateAsync(operation);
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);

        var updated = await store.GetAsync(operation.OperationId);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(
            WorkflowOperationStatus.ManualInterventionRequired,
            "a real backend rollback failure must escalate instead of parking in a non-terminal state");
        updated.CompletedAt.Should().NotBeNull();
        updated.CurrentPhase.Should().Contain("manual intervention");
        updated.ErrorMessage.Should().Contain("Automatic rollback failed");
        updated.ErrorMessage.Should().Contain(
            "ALB state lookup failed",
            "the production backend must return the sanitized AWS failure shape the reconciler escalates");
        updated.ErrorMessage.Should().NotContain(
            missingRuleArn,
            "provider ARNs and raw AWS details must stay out of the durable operator-facing error");
        backend.RollbackCalls.Should().Be(1);

        var endingShares = AwsEcsAlbCertificationSupport.ReadShares(
            await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region),
            canaryTargetGroup,
            stableTargetGroup);
        endingShares.Stable.Should().Be(
            AwsEcsAlbCertificationSupport.BaselineStableWeight,
            "the fault-injected rollback must fail before any ModifyRule mutation against the real substrate");
        endingShares.Canary.Should().Be(
            AwsEcsAlbCertificationSupport.BaselineCanaryWeight,
            "the fault-injected rollback must leave the canary weight unchanged");
    }

    private static string BuildMissingRuleArn(string listenerRuleArn)
    {
        var lastSlash = listenerRuleArn.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash == listenerRuleArn.Length - 1)
        {
            throw new InvalidOperationException(
                $"Unable to derive a syntactically valid missing rule ARN from '{listenerRuleArn}'.");
        }

        return listenerRuleArn[..(lastSlash + 1)] + Guid.NewGuid().ToString("N")[..16];
    }

    private sealed class RollbackRuleFaultInjectingBackend(
        AwsEcsAlbDeployBackend inner,
        string rollbackListenerRuleArn) : IDeployBackend
    {
        public int RollbackCalls { get; private set; }

        public string BackendName => inner.BackendName;

        public DeployTargetKind TargetKind => inner.TargetKind;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => inner.GetCapabilitiesAsync(cancellationToken);

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => inner.PlanAsync(spec, cancellationToken);

        public Task<DeploySubmissionResult> StartAsync(
            WorkflowOperationRecord operation,
            CancellationToken cancellationToken = default)
            => inner.StartAsync(operation, cancellationToken);

        public Task<DeployObservation> ObserveAsync(
            WorkflowOperationRecord operation,
            CancellationToken cancellationToken = default)
            => inner.ObserveAsync(operation, cancellationToken);

        public Task<DeployObservation> PromoteAsync(
            WorkflowOperationRecord operation,
            CancellationToken cancellationToken = default)
            => inner.PromoteAsync(operation, cancellationToken);

        public Task<DeployObservation> RollbackAsync(
            WorkflowOperationRecord operation,
            CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            var deploy = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
            var parameters = new Dictionary<string, string>(deploy.Parameters, StringComparer.Ordinal)
            {
                ["aws.alb.listener_rule_arn"] = rollbackListenerRuleArn
            };
            var faultedOperation = operation with
            {
                Deploy = deploy with { Parameters = parameters }
            };

            return inner.RollbackAsync(faultedOperation, cancellationToken);
        }
    }

    private sealed class SingleTargetRegistry(DeployOperationSpec deploy) : IDeployTargetRegistry
    {
        private readonly DeployTargetDefinition _target = new()
        {
            TargetId = deploy.TargetId,
            TargetKind = deploy.TargetKind,
            Backend = deploy.Backend,
            Environment = deploy.Environment,
            TargetName = deploy.TargetName,
            ArtifactReference = deploy.ArtifactReference,
            Parameters = deploy.Parameters
        };

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([_target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(targetId == _target.TargetId ? _target : null);
    }

    private sealed class StubDeployTelemetrySignalEvaluator(DeployTelemetryDecision? decision) : IDeployTelemetrySignalEvaluator
    {
        public Task<DeployTelemetryDecision?> EvaluateAsync(
            WorkflowOperationRecord operation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(decision);
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryAdd(operationId, ownerId));

        public Task<bool> RenewLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryGetValue(operationId, out var currentOwner) && currentOwner == ownerId);

        public Task ReleaseLeaseAsync(
            string operationId,
            string ownerId,
            CancellationToken cancellationToken = default)
        {
            _leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId));
            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(
            WorkflowOperationRecord operation,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryAdd(operation.OperationId, operation));

        public Task<WorkflowOperationRecord?> GetAsync(
            string operationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(
            string packageId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowOperationRecord?>(null);

        public Task SetAsync(
            WorkflowOperationRecord operation,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(
            WorkflowOperationRecord operation,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(
            WorkflowOperationKind? kind = null,
            CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => kind == null || operation.Kind == kind)
                .Where(operation => operation.Status is not (
                    WorkflowOperationStatus.Succeeded or
                    WorkflowOperationStatus.Failed or
                    WorkflowOperationStatus.RolledBack or
                    WorkflowOperationStatus.ManualInterventionRequired))
                .ToList();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }
    }
}
