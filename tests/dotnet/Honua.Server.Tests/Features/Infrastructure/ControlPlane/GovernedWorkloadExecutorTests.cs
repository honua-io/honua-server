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
    public void LambdaRecycle_RepublishesTheSameImage_AndRefusesADifferentOne()
    {
        var code = LambdaAliasRecycleRequests.UpdateCode("honua-prod-lambda", Image);
        code.ImageUri.Should().Be(Image);
        code.S3Bucket.Should().BeNull();
        code.ZipFile.Should().BeNull();

        var publish = LambdaAliasRecycleRequests.Publish("honua-prod-lambda");
        publish.FunctionName.Should().Be("honua-prod-lambda");

        var alias = LambdaAliasRecycleRequests.MoveAlias("honua-prod-lambda", "live", "44");
        alias.FunctionVersion.Should().Be("44");
        alias.RoutingConfig.Should().BeNull();

        LambdaImageGuard.RequireUnchanged(Image, Image).Should().Be(Image);
        var changed = () => LambdaImageGuard.RequireUnchanged(Image, Image.Replace("0123", "ffff", StringComparison.Ordinal));
        changed.Should().Throw<InvalidOperationException>().WithMessage(LambdaImageGuard.ChangedMessage);
        var mutable = () => LambdaImageGuard.RequireImmutable("honua:latest");
        mutable.Should().Throw<InvalidOperationException>().WithMessage("*immutable*");
    }

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

        public string? LambdaImage { get; private set; }

        public Task RestartEcsAsync(string cluster, string serviceName, string? region, CancellationToken cancellationToken = default)
        {
            Calls.Add("restart-ecs");
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
