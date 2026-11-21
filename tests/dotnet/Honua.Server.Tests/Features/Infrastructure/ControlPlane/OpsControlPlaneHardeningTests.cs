// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using Amazon.ECS;
using Amazon.ECS.Model;
using FluentAssertions;
using Task = System.Threading.Tasks.Task;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Db.Postgres.Features.Observability;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class OpsControlPlaneHardeningTests
{
    private const string Digest =
        "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string PriorTaskDef = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua:41";
    private const string CandidateTaskDef = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua:42";

    [UnitTest]
    public async Task AwsEcsBackend_SingleServiceRollingImageConvergence_PlansPreparesActivatesCompletesAndRollsBack()
    {
        var ecs = new SingleServiceEcsStub(PriorTaskDef, CandidateTaskDef);
        var alb = new UnusedAlbStub();
        var backend = new AwsEcsAlbDeployBackend(
            new AwsEcsAlbDeployClients(alb, ecs),
            NullLogger<AwsEcsAlbDeployBackend>.Instance);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aws.region"] = "us-east-1",
            ["aws.ecs.cluster"] = "honua-prod",
            ["aws.ecs.service"] = "honua-server",
        };
        var spec = new DeployOperationSpec
        {
            TargetId = "cfn-ecs",
            TargetKind = DeployTargetKind.AwsEcs,
            Backend = AwsEcsAlbDeployBackend.AdapterBackendName,
            Environment = "production",
            TargetName = "honua-server",
            ArtifactReference = Digest,
            DesiredRevision = Digest,
            Parameters = parameters,
        };

        var plan = await backend.PlanAsync(spec);
        plan.BlockingReasons.Should().BeEmpty();
        plan.IsReadyToSubmit.Should().BeTrue();

        var operation = new WorkflowOperationRecord
        {
            OperationId = "op-single-service",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new OperationAuditInfo(),
            Deploy = spec,
        };

        var checkpoint = await backend.PrepareCheckpointAsync(operation, CancellationToken.None);
        checkpoint.CandidateRevision.Should().Be(CandidateTaskDef);
        checkpoint.StableRevision.Should().Be(PriorTaskDef);
        checkpoint.OriginalServiceRevision.Should().Be(PriorTaskDef);

        var persisted = operation with
        {
            ProviderOperationId = checkpoint.CandidateRevision,
            ObservedState = checkpoint.OriginalServiceRevision,
            Deploy = spec with { CurrentRevision = checkpoint.StableRevision },
        };

        var activation = await backend.ActivateCheckpointAsync(persisted, CancellationToken.None);
        activation.Status.Should().Be(WorkflowOperationStatus.Submitted);
        ecs.CurrentTaskDefinition.Should().Be(CandidateTaskDef);

        var observed = await backend.ObserveAsync(persisted);
        observed.Status.Should().Be(WorkflowOperationStatus.Submitted);
        observed.PromotionRecommended.Should().BeTrue();

        var exposed = await backend.PromoteAsync(persisted);
        exposed.Status.Should().Be(WorkflowOperationStatus.Reconciling);

        var finalized = await backend.CompleteProtectionAsync(
            persisted with { Deploy = persisted.Deploy! with { TrafficExposedAt = DateTimeOffset.UtcNow } });
        finalized.Status.Should().Be(WorkflowOperationStatus.Succeeded);

        var rollback = await backend.RollbackAsync(persisted);
        rollback.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        ecs.CurrentTaskDefinition.Should().Be(PriorTaskDef);

        var settledRollback = await backend.ObserveAsync(persisted with { Status = WorkflowOperationStatus.RollbackRequested });
        settledRollback.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        settledRollback.ObservedRevision.Should().Be(PriorTaskDef);
    }

    [UnitTest]
    public async Task AwsEcsBackend_TransientErrorsInObserveAndRollback_KeepCurrentStatusForRetry()
    {
        var throttling = new AmazonECSException("Rate exceeded")
        {
            StatusCode = HttpStatusCode.ServiceUnavailable,
            ErrorCode = "ThrottlingException",
        };
        var ecs = new SingleServiceEcsStub(PriorTaskDef, CandidateTaskDef) { NextException = throttling };
        var backend = new AwsEcsAlbDeployBackend(
            new AwsEcsAlbDeployClients(new UnusedAlbStub(), ecs),
            NullLogger<AwsEcsAlbDeployBackend>.Instance);

        var operation = new WorkflowOperationRecord
        {
            OperationId = "op-transient",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            ProviderOperationId = CandidateTaskDef,
            ObservedState = PriorTaskDef,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new OperationAuditInfo(),
            Deploy = new DeployOperationSpec
            {
                TargetId = "cfn-ecs",
                TargetKind = DeployTargetKind.AwsEcs,
                Backend = AwsEcsAlbDeployBackend.AdapterBackendName,
                Environment = "production",
                TargetName = "honua-server",
                DesiredRevision = Digest,
                CurrentRevision = PriorTaskDef,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["aws.region"] = "us-east-1",
                    ["aws.ecs.cluster"] = "honua-prod",
                    ["aws.ecs.service"] = "honua-server",
                },
            },
        };

        var observeResult = await backend.ObserveAsync(operation);
        observeResult.Status.Should().Be(WorkflowOperationStatus.Reconciling);

        ecs.NextException = new HttpRequestException("transient network error");
        var rollbackResult = await backend.RollbackAsync(operation);
        rollbackResult.Status.Should().Be(WorkflowOperationStatus.Reconciling);
    }

    [UnitTest]
    public async Task DeployProbeUrlPolicy_AllowsPrivateVpcWhenEnabled_ButUnconditionallyBlocksImdsAndLinkLocal()
    {
        var vpcAllowed = await DeployProbeUrlPolicy.ValidateAsync(
            "http://internal-alb.vpc.local/healthz/ready",
            allowPrivateNetworks: true,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.12.45") }),
            CancellationToken.None);
        vpcAllowed.IsValid.Should().BeTrue();

        var imdsLiteral = await DeployProbeUrlPolicy.ValidateAsync(
            "http://169.254.169.254/latest/meta-data/",
            allowPrivateNetworks: true,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }),
            CancellationToken.None);
        imdsLiteral.IsValid.Should().BeFalse();

        var imdsResolved = await DeployProbeUrlPolicy.ValidateAsync(
            "https://rebind.internal/healthz",
            allowPrivateNetworks: true,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }),
            CancellationToken.None);
        imdsResolved.IsValid.Should().BeFalse();

        var ipv6LinkLocal = await DeployProbeUrlPolicy.ValidateAsync(
            "https://linklocal.internal/healthz",
            allowPrivateNetworks: true,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("fe80::1") }),
            CancellationToken.None);
        ipv6LinkLocal.IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void ControlPlaneNotReadyGuard_RefusesWhenMigrationsRunningOrFailed()
    {
        var migrationState = new MigrationState();
        migrationState.MarkRunning("applying");
        var runningContext = new DefaultHttpContext();

        var runningResult = ControlPlaneNotReadyGuard.TryRefuse(runningContext, degradedStartup: null, migrationState);
        runningResult.Should().NotBeNull();
        runningContext.Response.Headers[ReadinessReasonCodes.HeaderName].ToString()
            .Should().Be(ReadinessReasonCodes.MigrationsInProgress);

        migrationState.MarkFailed("boom");
        var failedContext = new DefaultHttpContext();
        var failedResult = ControlPlaneNotReadyGuard.TryRefuse(failedContext, degradedStartup: null, migrationState);
        failedResult.Should().NotBeNull();
        failedContext.Response.Headers[ReadinessReasonCodes.HeaderName].ToString()
            .Should().Be(ReadinessReasonCodes.MigrationsFailed);

        migrationState.MarkSucceeded("done");
        ControlPlaneNotReadyGuard.TryRefuse(new DefaultHttpContext(), degradedStartup: null, migrationState)
            .Should().BeNull();
    }

    [UnitTest]
    public async Task AdminConfigOperationExecutor_PlanAsync_PopulatesDeterministicServerOwnedDiffAndRedactsSecrets()
    {
        var executor = new AdminConfigOperationExecutor(Substitute.For<IAdminConfigChangeApplier>());
        var plan = await executor.PlanAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.AdminConfigChange,
            Reason = "Benign summary text",
            ExecutionPayload = """{"action":"alerts.redrive_dead_letters","target":"pagerduty","params":{"maxCount":50,"apiToken":"super-secret","channel":"ops"}}""",
        });

        plan.Should().NotBeNull();
        plan!.Diff.Should().Equal(
            "action: alerts.redrive_dead_letters",
            "target: pagerduty",
            "param.apiToken: [REDACTED]",
            "param.channel: ops",
            "param.maxCount: 50");
    }

    [UnitTest]
    public void ProposalEndpoints_SanitizeExecutionPayload_RedactsSecretsAndPreservesOperationalFields()
    {
        var sanitized = ProposalEndpoints.SanitizeExecutionPayload(
            """{"targetId":"ecs-prod","desiredRevision":"repo@sha256:abcd","authToken":"secret-val","nested":{"dbPassword":"pw","count":3}}""");

        sanitized.Should().NotBeNull();
        using var doc = JsonDocument.Parse(sanitized!);
        doc.RootElement.GetProperty("targetId").GetString().Should().Be("ecs-prod");
        doc.RootElement.GetProperty("authToken").GetString().Should().Be("[REDACTED]");
        doc.RootElement.GetProperty("nested").GetProperty("dbPassword").GetString().Should().Be("[REDACTED]");
        doc.RootElement.GetProperty("nested").GetProperty("count").GetInt32().Should().Be(3);
    }

    [UnitTest]
    public void AdminApiKeyPermission_OpsPropose_AuthorizesProposeAndReadWithoutGrantingAdminWrite()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(AdminApiKeyPermission.PermissionClaimType, AdminApiKeyPermission.OpsRead),
            new Claim(AdminApiKeyPermission.PermissionClaimType, AdminApiKeyPermission.OpsPropose),
        ],
        AuthenticationExtensions.ApiKeyScheme);
        var principal = new ClaimsPrincipal(identity);

        AdminApiKeyPermission.HasOpsReadGrant(principal).Should().BeTrue();
        AdminApiKeyPermission.HasOpsProposeGrant(principal).Should().BeTrue();
        AdminApiKeyPermission.IsOpsProposeAuthorized(principal).Should().BeTrue();
        AdminApiKeyPermission.ResolveAccessLevel(principal).Should().Be(AdminApiKeyPermission.AdminAccessLevel.None);
        AdminApiKeyPermission.IsAuthorized(principal, HttpMethods.Post).Should().BeFalse();
        AdminApiKeyPermission.IsAuthorized(principal, HttpMethods.Put).Should().BeFalse();
    }

    [UnitTest]
    public async Task OperationProposerApiKeyAuthority_AcceptsOpsProposeKeyAndSupportAgentActors()
    {
        var keyId = Guid.NewGuid();
        var store = Substitute.For<IAdminApiKeyStore>();
        store.GetAsync(keyId, Arg.Any<CancellationToken>()).Returns(new AdminApiKeyRecord
        {
            Id = keyId,
            Name = "support-breakglass",
            KeyPrefix = "honua_test",
            Permissions = [AdminApiKeyPermission.OpsRead, AdminApiKeyPermission.OpsPropose],
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var authority = new OperationProposerApiKeyAuthority(TimeProvider.System, store);

        var record = await authority.GetCurrentRecordAsync($"ApiKey:api-key:{keyId:D}", CancellationToken.None);
        record.Id.Should().Be(keyId);

        await authority.ValidateAsync("oauth:support-agent:ticket-42", CancellationToken.None);
    }

    [UnitTest]
    public void PostgresOpsAutonomyPolicyStore_BuildAuditDetails_ContainsPreviousAndCurrentSnapshotsAndDigests()
    {
        var previous = new OpsAutonomyPolicy
        {
            Rule = "alert-dispatch-backlog",
            Mode = OpsAutonomyMode.ProposeOnly,
            MaxAutoActionsPerWindow = 1,
            Window = TimeSpan.FromHours(1),
            MaxBlastRadius = 1,
        };
        var current = previous with
        {
            Mode = OpsAutonomyMode.AutoApply,
            MaxAutoActionsPerWindow = 3,
            Window = TimeSpan.FromMinutes(30),
            MaxBlastRadius = 2,
        };

        var json = PostgresOpsAutonomyPolicyStore.BuildPolicyAuditDetails(previous, current, "promoted from inbox");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("previous").GetProperty("mode").GetString().Should().Be("ProposeOnly");
        root.GetProperty("current").GetProperty("mode").GetString().Should().Be("AutoApply");
        root.GetProperty("current").GetProperty("maxAutoActionsPerWindow").GetInt32().Should().Be(3);
        root.GetProperty("current").GetProperty("windowSeconds").GetInt32().Should().Be(1800);
        root.GetProperty("current").GetProperty("maxBlastRadius").GetInt32().Should().Be(2);
        root.GetProperty("previousPolicyDigest").GetString().Should().HaveLength(64);
        root.GetProperty("newPolicyDigest").GetString().Should().HaveLength(64);
        root.GetProperty("previousPolicyDigest").GetString().Should().NotBe(root.GetProperty("newPolicyDigest").GetString());
        root.GetProperty("reason").GetString().Should().Be("promoted from inbox");
    }

    [UnitTest]
    public async Task OpsHeartbeatCoordinator_SkipsPublishWhenNotReady_AndReleasesLeaseOnPublishFailure()
    {
        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var publisher = Substitute.For<IOpsHeartbeatPublisher>();
        var readiness = Substitute.For<IReadinessCheckService>();
        readiness.CheckReadinessAsync(Arg.Any<CancellationToken>())
            .Returns(ReadinessResult.NotReady("migrations running", ReadinessReasonCodes.MigrationsInProgress));

        var services = new ServiceCollection();
        services.AddScoped(_ => readiness);
        await using var provider = services.BuildServiceProvider();

        var coordinator = new OpsHeartbeatCoordinator(jobStore, publisher, provider.GetRequiredService<IServiceScopeFactory>());
        await coordinator.PublishAsync("honua-stack", CancellationToken.None);

        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default);
        await jobStore.DidNotReceiveWithAnyArgs().TryAcquireLeaseAsync(default!, default!, default, default);

        readiness.CheckReadinessAsync(Arg.Any<CancellationToken>()).Returns(ReadinessResult.Ready());
        publisher.PublishAsync("honua-stack", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("CloudWatch down")));

        var act = () => coordinator.PublishAsync("honua-stack", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await jobStore.Received(1).ReleaseLeaseAsync("ops-heartbeat:honua-stack", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private sealed class SingleServiceEcsStub(string initialTaskDef, string registeredTaskDef) : IAwsEcsClient
    {
        public string CurrentTaskDefinition { get; private set; } = initialTaskDef;
        public Exception? NextException { get; set; }

        public Task<AwsEcsServiceState> DescribeServiceAsync(string cluster, string serviceName, string? region, CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            return Task.FromResult(new AwsEcsServiceState
            {
                ServiceName = serviceName,
                TaskDefinitionArn = CurrentTaskDefinition,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE",
                Deployments =
                [
                    new AwsEcsDeploymentState
                    {
                        Status = "PRIMARY",
                        TaskDefinitionArn = CurrentTaskDefinition,
                        RolloutState = "COMPLETED",
                        RunningCount = 2,
                        DesiredCount = 2,
                        PendingCount = 0,
                    },
                ],
            });
        }

        public Task UpdateServiceTaskDefinitionAsync(string cluster, string serviceName, string taskDefinitionArn, string? region, CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            CurrentTaskDefinition = taskDefinitionArn;
            return Task.CompletedTask;
        }

        public Task<TaskDefinition> DescribeTaskDefinitionAsync(string taskDefinition, string? region, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskDefinition
            {
                Family = "honua",
                ContainerDefinitions = [new ContainerDefinition { Name = "honua", Image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua:old" }],
            });

        public Task<string> RegisterTaskDefinitionAsync(RegisterTaskDefinitionRequest request, string? region, CancellationToken cancellationToken = default)
            => Task.FromResult(registeredTaskDef);

        private void ThrowIfConfigured()
        {
            if (NextException is not null)
            {
                var ex = NextException;
                NextException = null;
                throw ex;
            }
        }
    }

    private sealed class UnusedAlbStub : IAwsAlbClient
    {
        public Task<AwsAlbListenerRuleState> GetListenerRuleWeightsAsync(string ruleArn, string? region, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Single-service mode should not read dual-TG listener rules.");

        public Task<AwsAlbListenerRuleState> UpdateListenerRuleWeightsAsync(string ruleArn, IReadOnlyList<AwsAlbTargetGroupWeight> weights, string? region, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Single-service mode should not mutate dual-TG listener rules.");

        public Task<AwsAlbTargetHealthState> DescribeTargetHealthAsync(string targetGroupArn, string? region, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Single-service mode without target group should not query ALB target health.");
    }
}
