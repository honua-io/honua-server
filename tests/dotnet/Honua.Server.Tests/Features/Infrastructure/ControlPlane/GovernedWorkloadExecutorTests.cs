// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Governed restart and config actions. Separation of duties stays on
/// <c>OperationGateway</c>: the requester cannot approve the proposal. These tests
/// cover the product executor that runs after that approval. They do not call AWS.
/// </summary>
public sealed class GovernedWorkloadExecutorTests
{
    private const string TaskDefinitionArn = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua:41";
    private const string Image =
        "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task RestartEcs_ForcesNewDeployment_AndReturnsDurableId()
    {
        var harness = Harness(EcsTarget());

        var changeId = await harness.Applier.ApplyAsync("""{"action":"workload.restart_ecs","target":"prod-ecs"}""");

        changeId.Should().StartWith("opsaction-");
        harness.Executor.Calls.Should().Equal("restart-ecs");
    }

    [Fact]
    public async Task RestartEks_PatchesOnlyRestartedAt()
    {
        var harness = Harness(EksTarget());

        var changeId = await harness.Applier.ApplyAsync("""{"action":"workload.restart_eks","target":"prod-eks"}""");

        changeId.Should().StartWith("opsaction-");
        harness.Kubernetes.Calls.Should().Equal("honua/honua-server");
        harness.Executor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RestartLambda_RecyclesAliasWithoutANewImage()
    {
        var harness = Harness(LambdaTarget());

        var changeId = await harness.Applier.ApplyAsync("""{"action":"workload.restart_lambda","target":"prod-lambda"}""");

        changeId.Should().StartWith("opsaction-");
        harness.Executor.Calls.Should().Equal("restart-lambda");
        harness.Executor.LambdaImage.Should().Be(Image);
    }

    [Fact]
    public async Task SetConfig_ChangesDesiredCount_AndLeavesTaskDefinitionUnchanged()
    {
        var ecs = new RecordingEcs();
        var executor = new AwsGovernedWorkloadExecutor(ecs, new RecordingLambda());
        var harness = Harness(EcsTarget(), executor);

        var changeId = await harness.Applier.ApplyAsync(
            """{"action":"workload.set_config","target":"prod-ecs","params":{"field":"ecs.desired_count","value":2}}""");

        changeId.Should().StartWith("opsaction-");
        ecs.Calls.Should().Equal("describe", "count:2");
        ecs.TaskDefinitionArn.Should().Be(TaskDefinitionArn);
        ecs.DesiredCount.Should().Be(2);
    }

    [Fact]
    public async Task SetConfig_RejectsUnknownField_BeforeAnyWrite()
    {
        var harness = Harness(EcsTarget());

        var act = () => harness.Applier.ApplyAsync(
            """{"action":"workload.set_config","target":"prod-ecs","params":{"field":"ecs.image","value":2}}""");

        await act.Should().ThrowAsync<OpsActionException>().WithMessage(WorkloadExecutorCodes.ConfigFieldRejected);
        harness.Executor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownAction_ThrowsBeforeAnyWrite()
    {
        var harness = Harness(EcsTarget());

        var act = () => harness.Applier.ApplyAsync("""{"action":"workload.drop_database","target":"prod-ecs"}""");

        await act.Should().ThrowAsync<OpsActionException>().WithMessage("Unknown ops action 'workload.drop_database'.");
        harness.Executor.Calls.Should().BeEmpty();
        harness.Kubernetes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingExecutor_FailsClosedBeforeAnyWrite()
    {
        var harness = Harness(EcsTarget(), executor: null, defaultExecutor: false);

        var act = () => harness.Applier.ApplyAsync("""{"action":"workload.restart_ecs","target":"prod-ecs"}""");

        await act.Should().ThrowAsync<OpsActionException>().WithMessage(WorkloadExecutorCodes.Unconfigured);
        harness.EcsProbe.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UnregisteredTarget_AndKindMismatch_FailBeforeTheExecutor()
    {
        var harness = Harness(EcsTarget());

        var missing = () => harness.Applier.ApplyAsync("""{"action":"workload.restart_ecs","target":"missing"}""");
        await missing.Should().ThrowAsync<OpsActionException>().WithMessage(WorkloadExecutorCodes.TargetNotRegistered);

        var mismatch = () => harness.Applier.ApplyAsync("""{"action":"workload.restart_lambda","target":"prod-ecs"}""");
        await mismatch.Should().ThrowAsync<OpsActionException>().WithMessage(WorkloadExecutorCodes.TargetKindMismatch);
        harness.Executor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DesiredCountChange_ThatReplacesTaskDefinition_FailsClosed()
    {
        var ecs = new RecordingEcs { ChangeTaskDefinitionOnSet = true };
        var executor = new AwsGovernedWorkloadExecutor(ecs, new RecordingLambda());

        var act = () => executor.SetEcsDesiredCountAsync("honua-prod-ecs", "honua-prod-canary", 2, "us-east-1");

        await act.Should().ThrowAsync<OpsActionException>().WithMessage(WorkloadExecutorCodes.FingerprintChanged);
        ecs.Calls.Should().Contain("count:2");
        ecs.Calls.Should().NotContain("force");
    }

    [Fact]
    public void EcsRequests_SetOnlyTheGovernedBehavior()
    {
        var restart = EcsWorkloadUpdate.ForceNewDeployment("honua-prod-ecs", "honua-prod-canary");
        restart.ForceNewDeployment.Should().BeTrue();
        restart.TaskDefinition.Should().BeNull();
        restart.DesiredCount.Should().BeNull();
        restart.NetworkConfiguration.Should().BeNull();

        var count = EcsWorkloadUpdate.DesiredCount("honua-prod-ecs", "honua-prod-canary", 2);
        count.DesiredCount.Should().Be(2);
        count.ForceNewDeployment.Should().NotBe(true);
        count.TaskDefinition.Should().BeNull();
    }

    [Fact]
    public void LambdaRecycle_ChangesOnlyTheDescription_AndRefusesADifferentImage()
    {
        var revision = LambdaAliasRecycleRequests.ForceNewRevision("honua-prod-lambda", "honua-workload-restart:1");
        revision.FunctionName.Should().Be("honua-prod-lambda");
        revision.Description.Should().Be("honua-workload-restart:1");
        revision.Environment.Should().BeNull();
        revision.Role.Should().BeNull();
        revision.VpcConfig.Should().BeNull();
        revision.MemorySize.Should().BeNull();
        revision.Timeout.Should().BeNull();
        revision.KMSKeyArn.Should().BeNull();

        var publish = LambdaAliasRecycleRequests.Publish("honua-prod-lambda");
        publish.FunctionName.Should().Be("honua-prod-lambda");

        var alias = LambdaAliasRecycleRequests.MoveAlias("honua-prod-lambda", "live", "44", "revision-1");
        alias.FunctionVersion.Should().Be("44");
        alias.RevisionId.Should().Be("revision-1");
        alias.RoutingConfig.Should().BeNull();

        LambdaImageGuard.RequireUnchanged(Image, Image).Should().Be(Image);
        var changed = () => LambdaImageGuard.RequireUnchanged(Image, Image.Replace("0123", "ffff", StringComparison.Ordinal));
        changed.Should().Throw<InvalidOperationException>().WithMessage(LambdaImageGuard.ChangedMessage);
        var mutable = () => LambdaImageGuard.RequireImmutable("honua:latest");
        mutable.Should().Throw<InvalidOperationException>().WithMessage("*immutable*");
    }

    [Fact]
    public void LambdaWorkload_IgnoresDescription_AndRefusesEnvironmentDrift()
    {
        var serving = SampleWorkload();
        LambdaWorkloadIdentity.Same(serving, serving with { Description = "honua-workload-restart:1" }).Should().BeTrue();
        var drifted = serving with
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["HONUA_EXTRA"] = "1" }
        };
        LambdaWorkloadIdentity.Same(serving, drifted).Should().BeFalse();
        LambdaWorkloadIdentity.IsNewVersion("41", "41").Should().BeFalse();
        LambdaWorkloadIdentity.IsNewVersion("41", "42").Should().BeTrue();
    }

    [Fact]
    public async Task LambdaRecycle_RefusesDivergentConfiguration_BeforeAnyWrite()
    {
        var control = new FakeLambdaControl
        {
            Latest = SampleWorkload() with
            {
                Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["HONUA_EXTRA"] = "1" }
            }
        };

        var act = () => LambdaAliasRecycleExecution.ExecuteAsync(control, "honua-prod-lambda", "live", "honua-workload-restart:1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(LambdaWorkloadIdentity.DivergedMessage);
        control.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task LambdaRecycle_DoesNotMoveTheAlias_WhenPublishReturnsTheCurrentVersion()
    {
        var control = new FakeLambdaControl { PublishedVersion = "41" };

        var act = () => LambdaAliasRecycleExecution.ExecuteAsync(control, "honua-prod-lambda", "live", "honua-workload-restart:1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(LambdaWorkloadIdentity.SameVersionMessage);
        control.Writes.Should().Equal("description", "publish", "restore");
        control.MovedVersion.Should().BeNull();
    }

    [Fact]
    public async Task LambdaRecycle_MovesTheAlias_OnlyAfterANewMatchingVersion()
    {
        var control = new FakeLambdaControl();

        var image = await LambdaAliasRecycleExecution.ExecuteAsync(
            control,
            "honua-prod-lambda",
            "live",
            "honua-workload-restart:1",
            CancellationToken.None);

        image.Should().Be(Image);
        control.Writes.Should().Equal("description", "publish", "alias");
        control.MovedVersion.Should().Be("42");
        control.Descriptions.Should().ContainSingle().Which.Should().Be("honua-workload-restart:1");
    }

    [Fact]
    public async Task LambdaRecycle_RejectsConcurrentAliasChange()
    {
        var control = new FakeLambdaControl { ChangeAliasBeforeMove = true };
        var act = () => LambdaAliasRecycleExecution.ExecuteAsync(control, "honua-prod-lambda", "live", "restart", CancellationToken.None);
        await act.Should().ThrowAsync<Amazon.Lambda.Model.PreconditionFailedException>();
        control.MovedVersion.Should().BeNull();
    }

    [Fact]
    public async Task ConfigurationRegistry_ResolvesDeployTargetAddresses()
    {
        var executor = new RecordingExecutor();
        var kubernetes = new RecordingKubernetes();
        var services = new ServiceCollection();
        services.AddOptions<ControlPlaneOptions>().Configure(options =>
        {
            options.DeployTargets.Add(new DeployTargetOptions
            {
                TargetId = "prod-lambda",
                TargetKind = DeployTargetKind.AwsLambda,
                Backend = "honua-gitops-aws-lambda",
                Environment = "production",
                TargetName = "ignored-when-parameter-set",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["aws.lambda.function_name"] = "honua-prod-lambda",
                    ["aws.lambda.alias_name"] = "live",
                    ["aws.region"] = "us-east-1"
                }
            });
            options.DeployTargets.Add(new DeployTargetOptions
            {
                TargetId = "name-only-lambda",
                TargetKind = DeployTargetKind.AwsLambda,
                Backend = "honua-gitops-aws-lambda",
                Environment = "production",
                TargetName = "honua-from-target-name"
            });
            options.DeployTargets.Add(new DeployTargetOptions
            {
                TargetId = "prod-ecs",
                TargetKind = DeployTargetKind.AwsEcs,
                Backend = "honua-aws-ecs-alb",
                Environment = "production",
                TargetName = "honua-prod-canary",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["aws.ecs.cluster"] = "honua-prod-ecs"
                }
            });
            options.DeployTargets.Add(EksOptions("prod-eks", "honua-kubernetes-deployment"));
            options.DeployTargets.Add(EksOptions("gitops-eks", "honua-gitops-kubernetes"));
            options.DeployTargets.Add(EksOptions("argo-eks", "honua-kubernetes-argo-rollouts"));
        });
        services.AddSingleton<IDeployTargetRegistry, ConfigurationDeployTargetRegistry>();
        services.AddSingleton<IKubernetesWorkloadRestart>(kubernetes);
        services.AddSingleton<IGovernedWorkloadExecutor>(executor);
        var provider = services.BuildServiceProvider();
        var applier = new OpsActionAdminConfigChangeApplier(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<OpsActionAdminConfigChangeApplier>.Instance);

        await applier.ApplyAsync("""{"action":"workload.restart_lambda","target":"prod-lambda"}""");
        await applier.ApplyAsync("""{"action":"workload.restart_lambda","target":"name-only-lambda"}""");
        await applier.ApplyAsync("""{"action":"workload.restart_ecs","target":"prod-ecs"}""");
        await applier.ApplyAsync("""{"action":"workload.restart_eks","target":"prod-eks"}""");
        var gitops = () => applier.ApplyAsync("""{"action":"workload.restart_eks","target":"gitops-eks"}""");
        var argo = () => applier.ApplyAsync("""{"action":"workload.restart_eks","target":"argo-eks"}""");

        executor.LambdaCalls.Should().Equal("honua-prod-lambda:live", "honua-from-target-name:live");
        executor.EcsCalls.Should().Equal("honua-prod-ecs/honua-prod-canary");
        kubernetes.Calls.Should().Equal("honua/honua-server");
        await gitops.Should().ThrowAsync<OpsActionException>().WithMessage(WorkloadExecutorCodes.TargetKindMismatch);
        await argo.Should().ThrowAsync<OpsActionException>().WithMessage(WorkloadExecutorCodes.TargetKindMismatch);
        kubernetes.Calls.Should().Equal("honua/honua-server");
    }

    private static DeployTargetOptions EksOptions(string targetId, string backend)
        => new()
        {
            TargetId = targetId,
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = backend,
            Environment = "production",
            TargetName = "honua-server",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kubernetes.namespace"] = "honua",
                ["kubernetes.deployment_name"] = "honua-server"
            }
        };

    private static LambdaWorkloadSnapshot SampleWorkload()
        => new()
        {
            Image = Image,
            PackageType = "Image",
            Role = "arn:aws:iam::123456789012:role/honua-lambda",
            Timeout = 30,
            MemorySize = 512,
            Description = "serving",
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HONUA_SKIP_MIGRATIONS"] = "true"
            }
        };

    [Fact]
    public void KubernetesRestartPatch_SetsOnlyRestartedAt()
    {
        var patch = KubernetesRestartPatch.Serialize(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero));
        using var document = JsonDocument.Parse(patch);
        var annotations = document.RootElement
            .GetProperty("spec")
            .GetProperty("template")
            .GetProperty("metadata")
            .GetProperty("annotations");
        annotations.GetProperty(KubernetesRestartPatch.Annotation).GetString().Should().Be("2026-11-01T04:00:00Z");
        annotations.EnumerateObject().Select(property => property.Name).Should().Equal(KubernetesRestartPatch.Annotation);
        document.RootElement.GetProperty("spec").GetProperty("template").TryGetProperty("spec", out _).Should().BeFalse();
    }

    private static WorkloadHarness Harness(DeployTargetDefinition target, IGovernedWorkloadExecutor? executor = null, bool defaultExecutor = true)
    {
        var recording = executor as RecordingExecutor ?? new RecordingExecutor();
        var kubernetes = new RecordingKubernetes();
        var ecsProbe = new RecordingEcs();
        var services = new ServiceCollection();
        services.AddSingleton<IDeployTargetRegistry>(new FakeRegistry(target));
        services.AddSingleton<IKubernetesWorkloadRestart>(kubernetes);
        services.AddSingleton<IEcsWorkloadControl>(ecsProbe);
        if (executor is not null)
        {
            services.AddSingleton<IGovernedWorkloadExecutor>(executor);
        }
        else if (defaultExecutor)
        {
            services.AddSingleton<IGovernedWorkloadExecutor>(recording);
        }

        var provider = services.BuildServiceProvider();
        var applier = new OpsActionAdminConfigChangeApplier(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<OpsActionAdminConfigChangeApplier>.Instance);
        return new WorkloadHarness(applier, recording, kubernetes, ecsProbe);
    }

    private static DeployTargetDefinition EcsTarget()
        => new()
        {
            TargetId = "prod-ecs",
            TargetKind = DeployTargetKind.AwsEcs,
            Backend = "honua-aws-ecs-alb",
            Environment = "production",
            TargetName = "honua-prod-canary",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws.region"] = "us-east-1",
                ["aws.ecs.cluster"] = "honua-prod-ecs",
                ["aws.ecs.canary_service"] = "honua-prod-canary"
            }
        };

    private static DeployTargetDefinition EksTarget()
        => new()
        {
            TargetId = "prod-eks",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "honua-kubernetes-deployment",
            Environment = "production",
            TargetName = "honua-server",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kubernetes.namespace"] = "honua",
                ["kubernetes.deployment_name"] = "honua-server",
                ["kubernetes.container_name"] = "honua"
            }
        };

    private static DeployTargetDefinition LambdaTarget()
        => new()
        {
            TargetId = "prod-lambda",
            TargetKind = DeployTargetKind.AwsLambda,
            Backend = "honua-gitops-aws-lambda",
            Environment = "production",
            TargetName = "honua-prod-lambda",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aws.region"] = "us-east-1",
                ["lambda.function_name"] = "honua-prod-lambda",
                ["lambda.alias_name"] = "live"
            }
        };

    private sealed record WorkloadHarness(
        OpsActionAdminConfigChangeApplier Applier,
        RecordingExecutor Executor,
        RecordingKubernetes Kubernetes,
        RecordingEcs EcsProbe);

    private sealed class FakeRegistry(DeployTargetDefinition target) : IDeployTargetRegistry
    {
        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult<DeployTargetDefinition?>(string.Equals(target.TargetId, targetId, StringComparison.Ordinal) ? target : null);
    }

    private sealed class RecordingExecutor : IGovernedWorkloadExecutor
    {
        public List<string> Calls { get; } = [];

        public List<string> LambdaCalls { get; } = [];

        public List<string> EcsCalls { get; } = [];

        public string? LambdaImage { get; private set; }

        public Task RestartEcsAsync(string cluster, string serviceName, string? region, CancellationToken cancellationToken = default)
        {
            Calls.Add("restart-ecs");
            EcsCalls.Add($"{cluster}/{serviceName}");
            return Task.CompletedTask;
        }

        public Task SetEcsDesiredCountAsync(string cluster, string serviceName, int desiredCount, string? region, CancellationToken cancellationToken = default)
        {
            Calls.Add("set-count");
            return Task.CompletedTask;
        }

        public Task RestartLambdaAliasAsync(string functionName, string aliasName, string? region, string? serviceUrl, CancellationToken cancellationToken = default)
        {
            Calls.Add("restart-lambda");
            LambdaCalls.Add($"{functionName}:{aliasName}");
            LambdaImage = Image;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEcs : IEcsWorkloadControl
    {
        public List<string> Calls { get; } = [];

        public string TaskDefinitionArn { get; private set; } = GovernedWorkloadExecutorTests.TaskDefinitionArn;

        public int DesiredCount { get; private set; } = 1;

        public bool ChangeTaskDefinitionOnSet { get; init; }

        public Task<EcsWorkloadSnapshot> DescribeAsync(string cluster, string serviceName, string? region, CancellationToken cancellationToken = default)
        {
            Calls.Add("describe");
            return Task.FromResult(new EcsWorkloadSnapshot { TaskDefinitionArn = TaskDefinitionArn, DesiredCount = DesiredCount });
        }

        public Task ForceNewDeploymentAsync(string cluster, string serviceName, string? region, CancellationToken cancellationToken = default)
        {
            Calls.Add("force");
            return Task.CompletedTask;
        }

        public Task<EcsWorkloadSnapshot> SetDesiredCountAsync(string cluster, string serviceName, int desiredCount, string? region, CancellationToken cancellationToken = default)
        {
            Calls.Add($"count:{desiredCount}");
            if (ChangeTaskDefinitionOnSet)
            {
                TaskDefinitionArn += "-changed";
            }

            DesiredCount = desiredCount;
            return Task.FromResult(new EcsWorkloadSnapshot { TaskDefinitionArn = TaskDefinitionArn, DesiredCount = desiredCount });
        }
    }

    private sealed class RecordingLambda : ILambdaAliasRecycle
    {
        public Task<string> RecycleAsync(string functionName, string aliasName, string? region, string? serviceUrl, CancellationToken cancellationToken = default)
            => Task.FromResult(Image);
    }

    private sealed class FakeLambdaControl : ILambdaFunctionControl
    {
        public List<string> Writes { get; } = [];

        public List<string> Descriptions { get; } = [];

        public LambdaWorkloadSnapshot Serving { get; init; } = SampleWorkload();

        public LambdaWorkloadSnapshot Latest { get; init; } = SampleWorkload();

        public string PublishedVersion { get; init; } = "42";

        public string? MovedVersion { get; private set; }

        public string? AliasRevision { get; set; } = "revision-1";

        public bool ChangeAliasBeforeMove { get; set; }

        public Task<LambdaAliasView> ReadAliasAsync(string functionName, string aliasName, CancellationToken cancellationToken)
            => Task.FromResult(new LambdaAliasView(MovedVersion ?? "41", Serving.Image, AliasRevision));

        public Task<LambdaWorkloadSnapshot> ReadAsync(string functionName, string qualifier, CancellationToken cancellationToken)
            => Task.FromResult(qualifier == "$LATEST"
                ? Latest
                : qualifier == PublishedVersion
                    ? Serving
                    : Serving);

        public Task UpdateDescriptionAsync(string functionName, string description, CancellationToken cancellationToken)
        {
            Writes.Add(description == Serving.Description ? "restore" : "description");
            if (description != Serving.Description)
            {
                Descriptions.Add(description);
            }

            return Task.CompletedTask;
        }

        public Task<string> PublishAsync(string functionName, CancellationToken cancellationToken)
        {
            Writes.Add("publish");
            return Task.FromResult(PublishedVersion);
        }

        public Task MoveAliasAsync(string functionName, string aliasName, string functionVersion, string revisionId, CancellationToken cancellationToken)
        {
            if (ChangeAliasBeforeMove || revisionId != AliasRevision)
            {
                throw new Amazon.Lambda.Model.PreconditionFailedException("Alias revision changed");
            }

            Writes.Add("alias");
            MovedVersion = functionVersion;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingKubernetes : IKubernetesWorkloadRestart
    {
        public List<string> Calls { get; } = [];

        public Task RestartDeploymentAsync(string namespaceName, string deploymentName, DateTimeOffset restartedAt, CancellationToken cancellationToken = default)
        {
            Calls.Add($"{namespaceName}/{deploymentName}");
            return Task.CompletedTask;
        }
    }
}
