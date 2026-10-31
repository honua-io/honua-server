// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Amazon.ECS;
using Amazon.Runtime;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Fake-client contract for release-image convergence. The
/// <c>Category=ReleaseCellHook</c> facts are the hook honua-release eval #1164 runs
/// against a real cell through this same product executor. These tests do not call AWS.
/// </summary>
public sealed class ReleaseImageConvergenceTests
{
    private const string Digest =
        "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string PriorDigest =
        "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua@sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private const string Cluster = "honua-prod-ecs";
    private const string CanaryService = "honua-prod-canary";
    private const string CurrentTaskDef = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua:41";
    private const string RegisteredTaskDef = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua:42";
    private const string RegisteredMigrationTaskDef = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua-migrate:7";
    private const string ListenerRuleArn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener-rule/app/honua/abc/def/123";
    private const string CanaryTargetGroupArn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/honua-canary/abcdef";
    private const string StableTargetGroupArn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/honua-stable/abcdef";
    private const string SecretStopReason = "SecretStoppedReason";

    [Fact]
    public void ReleaseImageDigest_AcceptsImmutableReferences_AndRejectsTags()
    {
        ReleaseImageDigest.IsImmutable(Digest).Should().BeTrue();
        ReleaseImageDigest.IsImmutable("registry.example:5000/honua/server:2026.1@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
            .Should().BeTrue();
        ReleaseImageDigest.IsImmutable("honua:latest").Should().BeFalse();
        ReleaseImageDigest.IsImmutable("$LATEST").Should().BeFalse();
        ReleaseImageDigest.IsImmutable(Digest.Replace("abcdef", "ABCDEF", StringComparison.Ordinal)).Should().BeFalse();
        ReleaseImageDigest.IsEcsTaskDefinitionArn(CurrentTaskDef).Should().BeTrue();
        ReleaseImageDigest.IsEcsTaskDefinitionArn(Digest).Should().BeFalse();
    }

    [Fact]
    public void CloneWithImage_ChangesOnlyTheNamedContainerImage()
    {
        var source = new Amazon.ECS.Model.TaskDefinition
        {
            Family = "honua",
            TaskRoleArn = "arn:aws:iam::123456789012:role/honua-task",
            ExecutionRoleArn = "arn:aws:iam::123456789012:role/honua-exec",
            Cpu = "256",
            Memory = "512",
            ContainerDefinitions =
            [
                new Amazon.ECS.Model.ContainerDefinition
                {
                    Name = "honua",
                    Image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua:old",
                    Environment = [new Amazon.ECS.Model.KeyValuePair { Name = "HONUA_SKIP_MIGRATIONS", Value = "true" }],
                    Secrets = [new Amazon.ECS.Model.Secret { Name = "DB", ValueFrom = "arn:aws:secretsmanager:us-east-1:123456789012:secret:db" }],
                    LogConfiguration = new Amazon.ECS.Model.LogConfiguration { LogDriver = LogDriver.Awslogs }
                },
                new Amazon.ECS.Model.ContainerDefinition
                {
                    Name = "sidecar",
                    Image = "public.ecr.aws/honua/sidecar:1"
                }
            ]
        };

        var request = EcsTaskDefinitionImage.CloneWithImage(source, "honua", Digest);

        request.Family.Should().Be("honua");
        request.Cpu.Should().Be("256");
        request.TaskRoleArn.Should().Be(source.TaskRoleArn);
        request.ContainerDefinitions.Should().HaveCount(2);
        request.ContainerDefinitions[0].Image.Should().Be(Digest);
        request.ContainerDefinitions[0].Environment.Should().ContainSingle(pair => pair.Name == "HONUA_SKIP_MIGRATIONS" && pair.Value == "true");
        request.ContainerDefinitions[0].Secrets.Should().ContainSingle(secret => secret.Name == "DB");
        request.ContainerDefinitions[0].LogConfiguration!.LogDriver.Should().Be(LogDriver.Awslogs);
        request.ContainerDefinitions[1].Image.Should().Be("public.ecr.aws/honua/sidecar:1");
    }

    [Fact]
    [Trait("Category", "ReleaseCellHook")]
    public async Task EcsDigest_RegistersTaskDefinition_UpdatesService_AndReportsSteady()
    {
        var ecs = new DigestEcsClient();
        var alb = new DigestAlbClient();
        var backend = Backend(alb, ecs);
        var operation = EcsOperation(Digest);
        var submission = await backend.StartAsync(operation);
        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        ecs.Calls.Should().BeEmpty();
        var checkpoint = await backend.PrepareCheckpointAsync(operation, CancellationToken.None);
        checkpoint.CandidateRevision.Should().Be(RegisteredTaskDef);
        checkpoint.StableRevision.Should().Be(CurrentTaskDef);
        checkpoint.OriginalServiceRevision.Should().Be(CurrentTaskDef);
        ecs.UpdatedTaskDefinition.Should().BeNull();
        alb.UpdateCount.Should().Be(0);
        var persisted = Checkpointed(operation, checkpoint);
        var activation = await backend.ActivateCheckpointAsync(persisted, CancellationToken.None);
        activation.Status.Should().Be(WorkflowOperationStatus.Submitted);
        ecs.UpdatedTaskDefinition.Should().Be(RegisteredTaskDef);
        alb.UpdateCount.Should().Be(0);
        (await backend.ObserveAsync(persisted)).Status.Should().Be(WorkflowOperationStatus.Reconciling);
        alb.UpdateCount.Should().Be(1);
        (await backend.ObserveAsync(persisted)).Status.Should().Be(WorkflowOperationStatus.Succeeded);
    }

    [Fact]
    public async Task EcsActivation_WaitsForSteadyStateBeforeExposingTraffic()
    {
        var ecs = new DigestEcsClient { RolloutState = "IN_PROGRESS" };
        var alb = new DigestAlbClient();
        var backend = Backend(alb, ecs);
        var operation = EcsOperation(Digest);
        var checkpoint = await backend.PrepareCheckpointAsync(operation, CancellationToken.None);
        var persisted = Checkpointed(operation, checkpoint);
        await backend.ActivateCheckpointAsync(persisted, CancellationToken.None);
        var observation = await backend.ObserveAsync(persisted);
        observation.Status.Should().Be(WorkflowOperationStatus.Submitted);
        alb.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task EcsActivation_WaitsForHealthyTargetsBeforeTrafficShift()
    {
        var ecs = new DigestEcsClient();
        var alb = new DigestAlbClient { HealthyTargets = 0 };
        var backend = Backend(alb, ecs);
        var operation = EcsOperation(Digest);
        var checkpoint = await backend.PrepareCheckpointAsync(operation, CancellationToken.None);
        var persisted = Checkpointed(operation, checkpoint);
        await backend.ActivateCheckpointAsync(persisted, CancellationToken.None);
        var observation = await backend.ObserveAsync(persisted);
        observation.Status.Should().Be(WorkflowOperationStatus.Submitted);
        alb.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task EcsActivation_ServiceUpdateFailureDoesNotShiftTraffic()
    {
        var ecs = new DigestEcsClient { UpdateServiceException = new AmazonECSException("update failed") };
        var alb = new DigestAlbClient();
        var backend = Backend(alb, ecs);
        var operation = EcsOperation(Digest);
        var checkpoint = await backend.PrepareCheckpointAsync(operation, CancellationToken.None);
        var act = () => backend.ActivateCheckpointAsync(Checkpointed(operation, checkpoint), CancellationToken.None);
        await act.Should().ThrowAsync<AmazonECSException>();
        alb.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task EcsDigest_RunsMigrationBeforeServiceUpdate_AndBlocksOnNonZeroExit()
    {
        var ecs = new DigestEcsClient { MigrationExitCode = 1 };
        var parameters = EcsParameters();
        parameters["aws.ecs.migration_task_definition"] = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua-migrate:6";
        parameters["aws.ecs.migration_subnets"] = "subnet-a";
        parameters["aws.ecs.migration_security_groups"] = "sg-a";
        var act = () => Backend(new DigestAlbClient(), ecs).PrepareCheckpointAsync(EcsOperation(Digest, parameters), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        ecs.Calls.Should().Contain("run-task");
        ecs.Calls.Should().NotContain("update-service");
    }

    [Fact]
    public void MigrationExitCode_RequiresTheSelectedContainer()
    {
        var containers = new List<Amazon.ECS.Model.Container>
        {
            new() { Name = "sidecar", ExitCode = 0 },
            new() { Name = "honua-migrate" }
        };
        AwsSdkEcsClient.MigrationContainerExitCode(containers, "honua-migrate").Should().BeNull();
        AwsSdkEcsClient.MigrationContainerExitCode(containers, "missing").Should().BeNull();
        containers[1].ExitCode = 0;
        AwsSdkEcsClient.MigrationContainerExitCode(containers, "honua-migrate").Should().Be(0);
    }

    [Fact]
    public async Task EcsDigest_MigrationSuccess_UpdatesServiceOnlyAfterExitZero()
    {
        var ecs = new DigestEcsClient { MigrationExitCode = 0 };
        var parameters = EcsParameters();
        parameters["aws.ecs.migration_task_definition"] = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua-migrate:6";
        parameters["aws.ecs.migration_subnets"] = "subnet-a";
        parameters["aws.ecs.migration_security_groups"] = "sg-a";
        var backend = Backend(new DigestAlbClient(), ecs);
        var operation = EcsOperation(Digest, parameters);
        var checkpoint = await backend.PrepareCheckpointAsync(operation, CancellationToken.None);
        ecs.Calls.Should().Contain("run-task");
        ecs.Calls.Should().NotContain("update-service");
        await backend.ActivateCheckpointAsync(Checkpointed(operation, checkpoint), CancellationToken.None);
        ecs.Calls.IndexOf("run-task").Should().BeLessThan(ecs.Calls.IndexOf("update-service"));
    }

    [Fact]
    public async Task EcsDigest_MissingMigrationNetwork_FailsBeforeAnyTask()
    {
        var ecs = new DigestEcsClient();
        var parameters = EcsParameters();
        parameters["aws.ecs.migration_task_definition"] = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua-migrate:6";
        var backend = Backend(new DigestAlbClient(), ecs);
        var plan = await backend.PlanAsync(EcsSpec(Digest, parameters));
        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("migration_subnets", StringComparison.Ordinal));
        var act = () => backend.PrepareCheckpointAsync(EcsOperation(Digest, parameters), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        ecs.Calls.Should().NotContain("run-task");
    }

    [Fact]
    public async Task EcsDigest_MissingService_FailsAndDoesNotUpdate()
    {
        var ecs = new DigestEcsClient { DescribeServiceException = new AmazonECSException("ECS service was not found.") };
        var act = () => Backend(new DigestAlbClient(), ecs).PrepareCheckpointAsync(EcsOperation(Digest), CancellationToken.None);
        await act.Should().ThrowAsync<AmazonECSException>();
        ecs.Calls.Should().NotContain("update-service");
    }

    [Fact]
    public async Task EcsDigest_ProviderFailure_FailsClosedWithoutUpdate()
    {
        var ecs = new DigestEcsClient { RegisterException = new AmazonClientException("credential chain unavailable") };
        var act = () => Backend(new DigestAlbClient(), ecs).PrepareCheckpointAsync(EcsOperation(Digest), CancellationToken.None);
        await act.Should().ThrowAsync<AmazonClientException>();
        ecs.Calls.Should().NotContain("update-service");
    }

    [Fact]
    public async Task EcsTaskDefinitionArn_KeepsTheExistingUpdatePath()
    {
        var ecs = new DigestEcsClient();
        var submission = await Backend(new DigestAlbClient(), ecs).StartAsync(EcsOperation(CurrentTaskDef));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ProviderOperationId.Should().Be($"{Cluster}:{CanaryService}");
        ecs.UpdatedTaskDefinition.Should().Be(CurrentTaskDef);
        ecs.Calls.Should().NotContain("register");
    }

    [Fact]
    public async Task EcsDigest_FailedRollout_IsNotSuccess()
    {
        var ecs = new DigestEcsClient { RolloutState = "FAILED" };
        var backend = Backend(new DigestAlbClient(), ecs);
        var operation = EcsOperation(Digest);
        var checkpoint = await backend.PrepareCheckpointAsync(operation, CancellationToken.None);
        var persisted = Checkpointed(operation, checkpoint);
        await backend.ActivateCheckpointAsync(persisted, CancellationToken.None);
        var observation = await backend.ObserveAsync(persisted);
        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.RollbackRecommended.Should().BeTrue();
    }

    [Fact]
    public async Task EcsDigest_KeepsThePriorTaskDefinition_OnSteadyCanaryAndTimeout()
    {
        var ecs = new DigestEcsClient();
        var backend = Backend(new DigestAlbClient(), ecs);
        var operation = EcsOperation(Digest);
        var checkpoint = await backend.PrepareCheckpointAsync(operation, CancellationToken.None);
        var persisted = Checkpointed(operation, checkpoint);
        await backend.ActivateCheckpointAsync(persisted, CancellationToken.None);
        await backend.ActivateCheckpointAsync(persisted, CancellationToken.None);
        ecs.Calls.Count(call => call == "update-service").Should().Be(1);
        persisted.Deploy!.CurrentRevision.Should().Be(CurrentTaskDef);
        persisted.ProviderOperationId.Should().Be(RegisteredTaskDef);
    }

    [Fact]
    public async Task EcsDigest_ObserveWithoutRegisteredArn_StaysReconciling()
    {
        var ecs = new DigestEcsClient();
        var operation = EcsOperation(Digest) with { ProviderOperationId = $"{Cluster}:{CanaryService}" };

        var observation = await Backend(new DigestAlbClient(), ecs).ObserveAsync(operation);

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.Status.Should().NotBe(WorkflowOperationStatus.Succeeded);
    }

    [Fact]
    public async Task EcsRollbackWithoutPriorRevision_FailsClosed()
    {
        var observation = await Backend(new DigestAlbClient(), new DigestEcsClient())
            .RollbackAsync(EcsOperation(Digest, currentRevision: null));

        observation.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
    }

    [Fact]
    public async Task EcsDigest_PromoteUsesRegisteredRevision()
    {
        var ecs = new DigestEcsClient();
        var operation = EcsOperation(Digest) with { ProviderOperationId = RegisteredTaskDef };
        var result = await Backend(new DigestAlbClient(), ecs).PromoteAsync(operation);
        result.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        ecs.UpdatedTaskDefinition.Should().Be(RegisteredTaskDef);
    }

    [Fact]
    public async Task EcsApprovedRollback_IsADeployOfThePriorDigest()
    {
        var ecs = new DigestEcsClient();
        var checkpoint = await Backend(new DigestAlbClient(), ecs).PrepareCheckpointAsync(EcsOperation(PriorDigest), CancellationToken.None);
        ecs.RegisteredImages.Should().ContainSingle().Which.Should().Be(PriorDigest);
        checkpoint.CandidateRevision.Should().Be(RegisteredTaskDef);
    }

    [Fact]
    public void LambdaImagePublish_BindsToTheCodeAndRevisionReturnedByUpdate()
    {
        var request = AwsSdkLambdaImageClient.BuildPublishRequest("function", "code-sha", "revision");
        request.CodeSha256.Should().Be("code-sha");
        request.RevisionId.Should().Be("revision");
        var missing = () => AwsSdkLambdaImageClient.BuildPublishRequest("function", null, "revision");
        missing.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "ReleaseCellHook")]
    public async Task LambdaDigest_PublishesVersionThenMovesAlias()
    {
        var images = new DigestLambdaImageClient { PublishedVersion = "43" };
        var aliases = new DigestLambdaAliasClient();
        var backend = LambdaBackend(aliases, images);

        var submission = await backend.StartAsync(LambdaOperation(Digest));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ProviderOperationId.Should().Be("43");
        images.PublishedImage.Should().Be(Digest);
        aliases.LastUpdatedVersion.Should().Be("43");
        aliases.UpdateCount.Should().Be(1);

        var observation = await backend.ObserveAsync(LambdaOperation(Digest) with
        {
            ProviderOperationId = "43",
            Status = WorkflowOperationStatus.Reconciling
        });
        aliases.CurrentState = aliases.CurrentState with { FunctionVersion = "43" };
        observation = await backend.ObserveAsync(LambdaOperation(Digest) with
        {
            ProviderOperationId = "43",
            Status = WorkflowOperationStatus.Reconciling
        });
        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        observation.ObservedRevision.Should().Be(Digest);
    }

    [Fact]
    public async Task LambdaNumericVersion_DoesNotPublishAnImage()
    {
        var images = new DigestLambdaImageClient();
        var aliases = new DigestLambdaAliasClient();

        var submission = await LambdaBackend(aliases, images).StartAsync(LambdaOperation("42"));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        images.PublishCount.Should().Be(0);
        aliases.LastUpdatedVersion.Should().Be("42");
    }

    [Fact]
    public async Task LambdaPublishFailure_DoesNotMoveTheAlias()
    {
        var images = new DigestLambdaImageClient { Exception = new InvalidOperationException("update failed") };
        var aliases = new DigestLambdaAliasClient();

        var submission = await LambdaBackend(aliases, images).StartAsync(LambdaOperation(Digest));

        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        submission.Message.Should().Contain("alias was not moved");
        aliases.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task LambdaMissingFunction_FailsClosed()
    {
        var images = new DigestLambdaImageClient { Exception = new AmazonClientException("function missing") };
        var aliases = new DigestLambdaAliasClient();

        var submission = await LambdaBackend(aliases, images).StartAsync(LambdaOperation(Digest));

        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        submission.Message.Should().NotContain("function missing");
        aliases.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task LambdaDigestWithoutImageClient_IsNotReadyAndDoesNotMoveAlias()
    {
        var aliases = new DigestLambdaAliasClient();
        var backend = new AwsLambdaGitOpsDeployBackend(aliases, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);

        var plan = await backend.PlanAsync(LambdaSpec(Digest));
        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("not configured", StringComparison.Ordinal));
        plan.Warnings.Should().Contain(warning => warning.Contains("not implied", StringComparison.Ordinal));

        var submission = await backend.StartAsync(LambdaOperation(Digest));
        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        aliases.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task LambdaDigest_RefusesCanaryWeight()
    {
        var parameters = LambdaParameters();
        parameters["lambda.canary_weight_percentage"] = "10";
        parameters["telemetry.connection"] = "prod-prom";
        var images = new DigestLambdaImageClient();
        var backend = LambdaBackend(new DigestLambdaAliasClient(), images);

        var plan = await backend.PlanAsync(LambdaSpec(Digest, parameters));
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("canary", StringComparison.OrdinalIgnoreCase));

        var submission = await backend.StartAsync(LambdaOperation(Digest, parameters));
        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        images.PublishCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "ReleaseCellHook")]
    public async Task EksDigest_SetsContainerImage_AndWaitsUntilReady()
    {
        var client = new DigestKubernetesClient();
        var backend = new KubernetesDeploymentDeployBackend(client, NullLogger<KubernetesDeploymentDeployBackend>.Instance);

        var submission = await backend.StartAsync(EksOperation(Digest));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ProviderOperationId.Should().Be("honua/honua-server");
        submission.ObservedRevision.Should().Be(Digest);
        client.Image.Should().Be(Digest);
        client.PatchedContainer.Should().Be("honua");
        backend.BackendName.Should().Be("honua-kubernetes-deployment");
        (await backend.GetCapabilitiesAsync()).RequiresOutOfBandMigrations.Should().BeTrue();
    }

    [Fact]
    public async Task EksDigest_DoesNotSucceedUntilControllerObservesTheGeneration()
    {
        var client = new DigestKubernetesClient { StaleGetsRemaining = 1 };
        var backend = new KubernetesDeploymentDeployBackend(client, NullLogger<KubernetesDeploymentDeployBackend>.Instance);

        var early = await backend.StartAsync(EksOperation(Digest, steadyAttempts: 1));

        early.Status.Should().Be(WorkflowOperationStatus.Submitted);
        early.Status.Should().NotBe(WorkflowOperationStatus.Succeeded);
        client.Image.Should().Be(Digest);
        client.GetsAfterPatch.Should().Be(1);

        var ready = await backend.ObserveAsync(EksOperation(Digest));

        ready.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        ready.ObservedRevision.Should().Be(Digest);
    }

    [Fact]
    public async Task EksDigest_DoesNotSucceedWhileOldReplicasRemain()
    {
        var client = new DigestKubernetesClient { TotalReplicas = 3 };
        var backend = new KubernetesDeploymentDeployBackend(client, NullLogger<KubernetesDeploymentDeployBackend>.Instance);
        await backend.StartAsync(EksOperation(Digest, steadyAttempts: 1));

        var early = await backend.ObserveAsync(EksOperation(Digest));
        early.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        client.TotalReplicas = 2;
        var ready = await backend.ObserveAsync(EksOperation(Digest));
        ready.Status.Should().Be(WorkflowOperationStatus.Succeeded);
    }

    [Fact]
    public void KubernetesDeploymentImage_ParsesGenerationBeforeReplicaCountsCount()
    {
        using var stale = JsonDocument.Parse(
            """
            {
              "metadata": { "name": "honua-server", "generation": 4 },
              "spec": {
                "replicas": 2,
                "template": { "spec": { "containers": [ { "name": "honua", "image": "pinned" } ] } }
              },
              "status": { "observedGeneration": 3, "replicas": 3, "readyReplicas": 2, "updatedReplicas": 2 }
            }
            """);
        var parsed = KubernetesDeploymentImagePatch.Parse(stale.RootElement, "honua-server");
        parsed.Generation.Should().Be(4);
        parsed.ObservedGeneration.Should().Be(3);
        parsed.TotalReplicas.Should().Be(3);
        parsed.ReadyReplicas.Should().Be(2);
        parsed.ContainerImages["honua"].Should().Be("pinned");
    }

    [Fact]
    public async Task EksMissingDeployment_FailsBeforePatch()
    {
        var client = new DigestKubernetesClient { Missing = true };
        var submission = await new KubernetesDeploymentDeployBackend(client, NullLogger<KubernetesDeploymentDeployBackend>.Instance)
            .StartAsync(EksOperation(Digest));

        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        submission.Message.Should().Contain("was not found");
        client.PatchCount.Should().Be(0);
    }

    [Fact]
    public async Task EksProviderFailure_IsNotSuccess()
    {
        var client = new DigestKubernetesClient { Exception = new HttpRequestException("connection refused") };
        var submission = await new KubernetesDeploymentDeployBackend(client, NullLogger<KubernetesDeploymentDeployBackend>.Instance)
            .StartAsync(EksOperation(Digest));

        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        submission.Message.Should().NotContain("connection refused");
        client.PatchCount.Should().Be(0);
    }

    [Fact]
    public async Task EksUnconfiguredCluster_FailsClosed()
    {
        var client = new DigestKubernetesClient
        {
            Exception = new InvalidOperationException(
                "Kubernetes control-plane backend is configured but no API server endpoint is available.")
        };

        var submission = await new KubernetesDeploymentDeployBackend(client, NullLogger<KubernetesDeploymentDeployBackend>.Instance)
            .StartAsync(EksOperation(Digest));

        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        submission.Message.Should().Be("Kubernetes control plane is not configured for this deployment.");
        submission.Message.Should().NotContain("token");
    }

    [Fact]
    public async Task EksRollbackWithoutPrior_FailsClosed_AndPriorDigestIsANewDeploy()
    {
        var client = new DigestKubernetesClient();
        var backend = new KubernetesDeploymentDeployBackend(client, NullLogger<KubernetesDeploymentDeployBackend>.Instance);

        var missing = await backend.RollbackAsync(EksOperation(Digest, currentRevision: null));
        missing.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        client.PatchCount.Should().Be(0);

        var inflight = await backend.RollbackAsync(EksOperation(Digest, currentRevision: PriorDigest));
        inflight.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        inflight.Status.Should().NotBe(WorkflowOperationStatus.Succeeded);

        var rolled = await backend.StartAsync(EksOperation(PriorDigest));
        rolled.Status.Should().Be(WorkflowOperationStatus.Submitted);
        client.Image.Should().Be(PriorDigest);
    }

    [Fact]
    public void KubernetesImagePatch_SetsOnlyTheNamedContainerImage()
    {
        var patch = KubernetesDeploymentImagePatch.SerializeSetImage("honua", Digest);
        using var document = JsonDocument.Parse(patch);
        var container = document.RootElement.GetProperty("spec").GetProperty("template").GetProperty("spec").GetProperty("containers")[0];
        container.GetProperty("name").GetString().Should().Be("honua");
        container.GetProperty("image").GetString().Should().Be(Digest);
        container.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(["name", "image"]);
    }

    [Fact]
    public async Task GitOpsHandoff_IsNotACompletedRollout()
    {
        var ecsGitOps = new AwsEcsGitOpsDeployBackend(NullLogger<AwsEcsGitOpsDeployBackend>.Instance);
        var started = await ecsGitOps.StartAsync(EcsOperation(Digest));
        started.Status.Should().NotBe(WorkflowOperationStatus.Succeeded);
        started.Message.Should().Contain("GitOps");

        var observed = await ecsGitOps.ObserveAsync(EcsOperation(Digest));
        observed.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);

        var lambdaGitOps = new AwsLambdaGitOpsDeployBackend(
            new DigestLambdaAliasClient(),
            NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);
        var lambdaObserved = await lambdaGitOps.ObserveAsync(LambdaOperation("41") with
        {
            ProviderOperationId = null,
            Status = WorkflowOperationStatus.Reconciling
        });
        lambdaObserved.Status.Should().NotBe(WorkflowOperationStatus.ManualInterventionRequired);
    }

    private static WorkflowOperationRecord Checkpointed(WorkflowOperationRecord operation, Honua.Core.Features.ControlPlane.Abstractions.DeployCheckpoint checkpoint)
        => operation with
        {
            ProviderOperationId = checkpoint.CandidateRevision,
            ObservedState = checkpoint.OriginalServiceRevision,
            Deploy = operation.Deploy! with { CurrentRevision = checkpoint.StableRevision }
        };

    private static AwsEcsAlbDeployBackend Backend(DigestAlbClient alb, DigestEcsClient ecs)
        => new(alb, ecs, NullLogger<AwsEcsAlbDeployBackend>.Instance);

    private static AwsLambdaGitOpsDeployBackend LambdaBackend(DigestLambdaAliasClient aliases, DigestLambdaImageClient images)
        => new(aliases, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance, imageClient: images);

    private static Dictionary<string, string> EcsParameters()
        => new(StringComparer.Ordinal)
        {
            ["aws.region"] = "us-east-1",
            ["aws.ecs.cluster"] = Cluster,
            ["aws.ecs.canary_service"] = CanaryService,
            ["aws.ecs.stable_service"] = "honua-prod-stable",
            ["aws.alb.listener_rule_arn"] = ListenerRuleArn,
            ["aws.alb.canary_target_group_arn"] = CanaryTargetGroupArn,
            ["aws.alb.stable_target_group_arn"] = StableTargetGroupArn,
            ["aws.ecs.steady_delay_ms"] = "0",
            ["aws.ecs.steady_attempts"] = "2"
        };

    private static Dictionary<string, string> SteadyFast()
    {
        var parameters = EcsParameters();
        parameters["aws.ecs.steady_attempts"] = "1";
        return parameters;
    }

    private static DeployOperationSpec EcsSpec(string desired, IReadOnlyDictionary<string, string>? parameters = null)
        => new()
        {
            TargetId = "prod-ecs",
            TargetKind = DeployTargetKind.AwsEcs,
            Backend = AwsEcsAlbDeployBackend.AdapterBackendName,
            Environment = "production",
            TargetName = CanaryService,
            ArtifactReference = Digest,
            DesiredRevision = desired,
            RequiresOutOfBandMigrations = true,
            Parameters = parameters ?? EcsParameters()
        };

    private static WorkflowOperationRecord EcsOperation(
        string desired,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? currentRevision = CurrentTaskDef)
        => Operation(EcsSpec(desired, parameters) with { CurrentRevision = currentRevision }, "production:prod-ecs");

    private static Dictionary<string, string> LambdaParameters()
        => new(StringComparer.Ordinal)
        {
            ["lambda.alias_name"] = "live",
            ["lambda.function_name"] = "honua-prod-lambda",
            ["aws.region"] = "us-east-1"
        };

    private static DeployOperationSpec LambdaSpec(string desired, IReadOnlyDictionary<string, string>? parameters = null)
        => new()
        {
            TargetId = "prod-lambda",
            TargetKind = DeployTargetKind.AwsLambda,
            Backend = "honua-gitops-aws-lambda",
            Environment = "production",
            TargetName = "honua-prod-lambda",
            ArtifactReference = Digest,
            DesiredRevision = desired,
            RequiresOutOfBandMigrations = true,
            Parameters = parameters ?? LambdaParameters()
        };

    private static WorkflowOperationRecord LambdaOperation(string desired, IReadOnlyDictionary<string, string>? parameters = null)
        => Operation(LambdaSpec(desired, parameters) with { CurrentRevision = "41" }, "production:prod-lambda");

    private static WorkflowOperationRecord EksOperation(string desired, string? currentRevision = PriorDigest, int steadyAttempts = 2)
        => Operation(new DeployOperationSpec
        {
            TargetId = "prod-eks",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = KubernetesDeploymentDeployBackend.AdapterBackendName,
            Environment = "production",
            TargetName = "honua-server",
            ArtifactReference = desired,
            DesiredRevision = desired,
            RequiresOutOfBandMigrations = true,
            CurrentRevision = currentRevision,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kubernetes.namespace"] = "honua",
                ["kubernetes.deployment_name"] = "honua-server",
                ["kubernetes.container_name"] = "honua",
                ["kubernetes.steady_delay_ms"] = "0",
                ["kubernetes.steady_attempts"] = steadyAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        }, "production:prod-eks");

    private static WorkflowOperationRecord Operation(DeployOperationSpec spec, string partition)
        => new()
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CurrentPhase = "Testing",
            Audit = new OperationAuditInfo(),
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = partition,
                RequiresExclusiveLease = true
            },
            Deploy = spec
        };

    private static Amazon.ECS.Model.TaskDefinition ServingDefinition()
        => new()
        {
            Family = "honua",
            Cpu = "256",
            Memory = "512",
            NetworkMode = NetworkMode.Awsvpc,
            RequiresCompatibilities = ["FARGATE"],
            ContainerDefinitions =
            [
                new Amazon.ECS.Model.ContainerDefinition
                {
                    Name = "honua",
                    Image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua:old",
                    Essential = true,
                    Environment = [new Amazon.ECS.Model.KeyValuePair { Name = "HONUA_SKIP_MIGRATIONS", Value = "true" }],
                    Secrets = [new Amazon.ECS.Model.Secret { Name = "DB", ValueFrom = "arn:aws:secretsmanager:us-east-1:123456789012:secret:db" }]
                },
                new Amazon.ECS.Model.ContainerDefinition { Name = "sidecar", Image = "public.ecr.aws/honua/sidecar:1", Essential = false }
            ]
        };

    private static Amazon.ECS.Model.TaskDefinition MigrationDefinition()
        => new()
        {
            Family = "honua-migrate",
            Cpu = "256",
            Memory = "512",
            ContainerDefinitions =
            [
                new Amazon.ECS.Model.ContainerDefinition
                {
                    Name = "honua-migrate",
                    Image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua:old",
                    Environment = [new Amazon.ECS.Model.KeyValuePair { Name = "HONUA_MIGRATE_ONLY", Value = "true" }]
                }
            ]
        };

    private sealed class DigestAlbClient : IAwsAlbClient
    {
        public int UpdateCount { get; private set; }

        private AwsAlbListenerRuleState _rule = new()
        {
            ListenerRuleArn = ListenerRuleArn,
            TargetGroupWeights =
            [
                new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 0 },
                new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 100 }
            ]
        };

        public Task<AwsAlbListenerRuleState> GetListenerRuleWeightsAsync(string ruleArn, string? region, CancellationToken cancellationToken = default)
            => Task.FromResult(_rule);

        public Task<AwsAlbListenerRuleState> UpdateListenerRuleWeightsAsync(
            string ruleArn,
            IReadOnlyList<AwsAlbTargetGroupWeight> weights,
            string? region,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            _rule = new AwsAlbListenerRuleState { ListenerRuleArn = ruleArn, TargetGroupWeights = weights };
            return Task.FromResult(_rule);
        }

        public int HealthyTargets { get; init; } = 2;

        public Task<AwsAlbTargetHealthState> DescribeTargetHealthAsync(string targetGroupArn, string? region, CancellationToken cancellationToken = default)
            => Task.FromResult(new AwsAlbTargetHealthState
            {
                Targets = Enumerable.Range(0, HealthyTargets).Select(_ => new AwsAlbTargetHealth { TargetId = "target", State = "healthy" }).ToArray()
            });
    }

    private sealed class DigestEcsClient : IAwsEcsClient
    {
        public List<string> Calls { get; } = [];

        public List<string> RegisteredImages { get; } = [];

        public string? UpdatedTaskDefinition { get; private set; }

        public string? RanTaskDefinition { get; private set; }

        public Exception? DescribeServiceException { get; init; }

        public Exception? RegisterException { get; init; }

        public Exception? UpdateServiceException { get; init; }

        public int MigrationExitCode { get; init; }

        public string MigrationLastStatus { get; init; } = "STOPPED";

        public string RolloutState { get; init; } = "COMPLETED";

        public Task<AwsEcsServiceState> DescribeServiceAsync(string cluster, string serviceName, string? region, CancellationToken cancellationToken = default)
        {
            Calls.Add("describe-service");
            if (DescribeServiceException is not null)
            {
                throw DescribeServiceException;
            }

            var arn = serviceName == "honua-prod-stable" ? CurrentTaskDef : UpdatedTaskDefinition ?? CurrentTaskDef;
            return Task.FromResult(new AwsEcsServiceState
            {
                ServiceName = serviceName,
                TaskDefinitionArn = arn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE",
                Deployments =
                [
                    new AwsEcsDeploymentState
                    {
                        Status = "PRIMARY",
                        TaskDefinitionArn = arn,
                        RolloutState = UpdatedTaskDefinition is null ? "COMPLETED" : RolloutState,
                        RunningCount = 2,
                        DesiredCount = 2,
                        PendingCount = 0
                    }
                ]
            });
        }

        public Task UpdateServiceTaskDefinitionAsync(string cluster, string serviceName, string taskDefinitionArn, string? region, CancellationToken cancellationToken = default)
        {
            Calls.Add("update-service");
            if (UpdateServiceException is not null) throw UpdateServiceException;
            UpdatedTaskDefinition = taskDefinitionArn;
            return Task.CompletedTask;
        }

        public Task<Amazon.ECS.Model.TaskDefinition> DescribeTaskDefinitionAsync(string taskDefinition, string? region, CancellationToken cancellationToken = default)
        {
            Calls.Add("describe-task");
            return Task.FromResult(taskDefinition.Contains("migrate", StringComparison.Ordinal)
                ? MigrationDefinition()
                : ServingDefinition());
        }

        public Task<string> RegisterTaskDefinitionAsync(Amazon.ECS.Model.RegisterTaskDefinitionRequest request, string? region, CancellationToken cancellationToken = default)
        {
            Calls.Add("register");
            if (RegisterException is not null)
            {
                throw RegisterException;
            }

            var image = request.ContainerDefinitions.Single(container => container.Image == Digest || container.Image == PriorDigest).Image;
            RegisteredImages.Add(image);
            var migration = request.Family == "honua-migrate";
            if (migration)
            {
                request.ContainerDefinitions.Should().ContainSingle(container =>
                    container.Name == "honua-migrate" && container.Environment.Any(pair => pair.Name == "HONUA_MIGRATE_ONLY"));
            }
            else
            {
                request.ContainerDefinitions.Should().Contain(container => container.Name == "sidecar" && container.Image == "public.ecr.aws/honua/sidecar:1");
            }

            return Task.FromResult(migration ? RegisteredMigrationTaskDef : RegisteredTaskDef);
        }

        public Task<string> RunTaskAsync(
            string cluster,
            string taskDefinitionArn,
            IReadOnlyList<string> subnets,
            IReadOnlyList<string> securityGroups,
            string? region,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("run-task");
            RanTaskDefinition = taskDefinitionArn;
            subnets.Should().NotBeEmpty();
            securityGroups.Should().NotBeEmpty();
            return Task.FromResult("arn:aws:ecs:us-east-1:123456789012:task/honua-prod-ecs/migrate");
        }

        public Task<AwsEcsStoppedTask> DescribeStoppedTaskAsync(string cluster, string taskArn, string containerName, string? region, CancellationToken cancellationToken = default)
        {
            containerName.Should().Be("honua-migrate");
            Calls.Add("describe-stopped");
            return Task.FromResult(new AwsEcsStoppedTask
            {
                LastStatus = MigrationLastStatus,
                ExitCode = string.Equals(MigrationLastStatus, "STOPPED", StringComparison.Ordinal) ? MigrationExitCode : null
            });
        }
    }

    private sealed class DigestLambdaAliasClient : IAwsLambdaAliasClient
    {
        public AwsLambdaAliasState CurrentState { get; set; } = new()
        {
            AliasName = "live",
            AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua:live",
            FunctionVersion = "41"
        };

        public string? LastUpdatedVersion { get; private set; }

        public int UpdateCount { get; private set; }

        public Task<AwsLambdaAliasState> GetAliasAsync(string functionName, string aliasName, string? region, string? serviceUrl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentState);

        public Task<AwsLambdaAliasState> UpdateAliasAsync(
            string functionName,
            string aliasName,
            string functionVersion,
            IReadOnlyDictionary<string, double>? additionalVersionWeights,
            string? region,
            string? serviceUrl = null,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            LastUpdatedVersion = functionVersion;
            CurrentState = CurrentState with { FunctionVersion = functionVersion };
            return Task.FromResult(CurrentState);
        }
    }

    private sealed class DigestLambdaImageClient : IAwsLambdaImageClient
    {
        public string PublishedVersion { get; init; } = "43";

        public Exception? Exception { get; init; }

        public string? PublishedImage { get; private set; }

        public int PublishCount { get; private set; }

        public Task<string> PublishImageAsync(
            string functionName,
            string imageUri,
            string? region,
            string? serviceUrl,
            int maxAttempts,
            int delayMilliseconds,
            CancellationToken cancellationToken = default)
        {
            PublishCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            PublishedImage = imageUri;
            return Task.FromResult(PublishedVersion);
        }
    }

    private sealed class DigestKubernetesClient : IKubernetesDeploymentImageClient
    {
        public bool Missing { get; init; }

        public Exception? Exception { get; init; }

        public string? Image { get; private set; }

        public string? PatchedContainer { get; private set; }

        public int PatchCount { get; private set; }

        public int GetsAfterPatch { get; private set; }

        public int StaleGetsRemaining { get; set; }

        public int TotalReplicas { get; set; } = 2;

        public long Generation { get; set; } = 2;

        public Task<KubernetesDeploymentImageState?> GetAsync(string namespaceName, string deploymentName, CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            if (Missing)
            {
                return Task.FromResult<KubernetesDeploymentImageState?>(null);
            }

            var image = Image ?? "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua:old";
            var patched = Image is not null;
            var observedGeneration = Generation;
            if (patched)
            {
                GetsAfterPatch++;
                if (StaleGetsRemaining > 0)
                {
                    observedGeneration = Generation - 1;
                    StaleGetsRemaining--;
                }
            }

            return Task.FromResult<KubernetesDeploymentImageState?>(new KubernetesDeploymentImageState
            {
                Name = deploymentName,
                Replicas = 2,
                TotalReplicas = patched ? TotalReplicas : 0,
                ReadyReplicas = patched ? 2 : 0,
                UpdatedReplicas = patched ? 2 : 0,
                Generation = Generation,
                ObservedGeneration = observedGeneration,
                ContainerImages = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["honua"] = image,
                    ["sidecar"] = "public.ecr.aws/honua/sidecar:1"
                }
            });
        }

        public Task SetContainerImageAsync(string namespaceName, string deploymentName, string containerName, string image, CancellationToken cancellationToken = default)
        {
            PatchCount++;
            PatchedContainer = containerName;
            Image = image;
            return Task.CompletedTask;
        }
    }
}
