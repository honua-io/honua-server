// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Trait("Tier", "Fast")]
public sealed class LambdaImageCheckpointTests
{
    private const string Image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void PublishRequest_RequiresAndBindsUpdatePreconditions()
    {
        var request = AwsSdkLambdaImageClient.BuildPublishRequest("honua", "hash", "revision");
        request.CodeSha256.Should().Be("hash");
        request.RevisionId.Should().Be("revision");
        var missing = () => AwsSdkLambdaImageClient.BuildPublishRequest("honua", null, "revision");
        missing.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Preparation_CheckpointsPublishedVersionAndStableAliasBeforeCutover()
    {
        var client = new FakeImageClient();
        var backend = CreateBackend(client);
        var operation = CreateOperation();
        var checkpoint = await backend.PrepareCheckpointAsync(operation, default);
        checkpoint.CandidateRevision.Should().Be("43");
        checkpoint.StableRevision.Should().Be("41");
        checkpoint.OriginalServiceRevision.Should().Be("rev1");
        client.Moves.Should().Be(0);
        var prepared = operation with { ProviderOperationId = checkpoint.CandidateRevision, ObservedState = checkpoint.OriginalServiceRevision, Deploy = operation.Deploy! with { CurrentRevision = checkpoint.StableRevision } };
        (await backend.ActivateCheckpointAsync(prepared, default)).Status.Should().Be(WorkflowOperationStatus.Submitted);
        client.Moves.Should().Be(0);
        (await backend.PromoteAsync(prepared)).Status.Should().Be(WorkflowOperationStatus.Reconciling);
        client.Moves.Should().Be(1);
        (await backend.PromoteAsync(prepared)).Status.Should().Be(WorkflowOperationStatus.Reconciling);
        client.Moves.Should().Be(1);
    }

    [Fact]
    public async Task Cutover_RefusesConcurrentAliasUpdate()
    {
        var client = new FakeImageClient();
        var backend = CreateBackend(client);
        var operation = CreateOperation() with { ProviderOperationId = "43", ObservedState = "rev1", Deploy = CreateOperation().Deploy! with { CurrentRevision = "41" } };
        client.Alias = new("41", "other-revision", false);
        var move = () => backend.PromoteAsync(operation);
        await move.Should().ThrowAsync<InvalidOperationException>();
        client.Moves.Should().Be(0);
    }

    [Fact]
    public async Task TransientAliasFailure_PreservesObservationAndCutoverForRetry()
    {
        var client = new FakeImageClient { FailAliasRead = true };
        var backend = CreateBackend(client);
        var operation = CreateOperation() with { ProviderOperationId = "43", ObservedState = "rev1", Deploy = CreateOperation().Deploy! with { CurrentRevision = "41" } };
        var observation = await backend.ObserveAsync(operation);
        observation.Status.Should().Be(WorkflowOperationStatus.Submitted);
        observation.Message.Should().Contain("retry");
        var promotion = await backend.PromoteAsync(operation);
        promotion.Status.Should().Be(WorkflowOperationStatus.Submitted);
        promotion.Message.Should().Be(Honua.Core.Features.ControlPlane.Abstractions.ICheckpointedDeployBackend.CutoverPhase);
        client.Moves.Should().Be(0);
    }

    [Fact]
    public async Task Preparation_RefusesAliasChangeDuringPublication()
    {
        var client = new FakeImageClient { ChangeAliasOnPublish = true };
        var backend = CreateBackend(client);
        var prepare = () => backend.PrepareCheckpointAsync(CreateOperation(), default);
        await prepare.Should().ThrowAsync<InvalidOperationException>();
        client.Moves.Should().Be(0);
    }

    [Fact]
    public async Task Preparation_RefusesWeightedAliasBeforePublishing()
    {
        var client = new FakeImageClient { Alias = new("41", "rev1", true) };
        var backend = CreateBackend(client);
        var prepare = () => backend.PrepareCheckpointAsync(CreateOperation(), default);
        await prepare.Should().ThrowAsync<InvalidOperationException>();
        client.PublishCount.Should().Be(0);
    }

    [Fact]
    public async Task Preparation_RefusesCanaryWithoutPublishing()
    {
        var client = new FakeImageClient();
        var backend = CreateBackend(client);
        var operation = CreateOperation();
        var parameters = new Dictionary<string, string>(operation.Deploy!.Parameters) { ["lambda.canary_weight_percentage"] = "10" };
        var invalid = operation with { Deploy = operation.Deploy with { Parameters = parameters } };
        var prepare = () => backend.PrepareCheckpointAsync(invalid, default);
        await prepare.Should().ThrowAsync<InvalidOperationException>();
        client.PublishCount.Should().Be(0);
    }

    private static AwsLambdaGitOpsDeployBackend CreateBackend(FakeImageClient client)
        => new(new FakeAliasClient(), NullLogger<AwsLambdaGitOpsDeployBackend>.Instance, imageClient: client);

    private static WorkflowOperationRecord CreateOperation() => new()
    {
        OperationId = "deploy-1",
        Kind = WorkflowOperationKind.Deploy,
        Status = WorkflowOperationStatus.Submitted,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Deploy = new DeployOperationSpec { TargetId = "lambda", TargetKind = DeployTargetKind.AwsLambda, Backend = "honua-gitops-aws-lambda", Environment = "production", TargetName = "honua", DesiredRevision = Image, Parameters = new Dictionary<string, string> { ["lambda.alias_name"] = "live" } }
    };

    private sealed class FakeImageClient : IAwsLambdaImageClient
    {
        public LambdaImageAlias Alias { get; set; } = new("41", "rev1", false);
        public int Moves { get; private set; }
        public int PublishCount { get; private set; }
        public bool ChangeAliasOnPublish { get; init; }
        public bool FailAliasRead { get; init; }
        public Task<LambdaImageAlias> GetAliasAsync(string functionName, string aliasName, string? region, string? serviceUrl, CancellationToken cancellationToken) => FailAliasRead ? throw new Amazon.Runtime.AmazonClientException("transient") : Task.FromResult(Alias);
        public Task MoveAliasAsync(string functionName, string aliasName, string version, string expectedRevisionId, string? region, string? serviceUrl, CancellationToken cancellationToken)
        {
            expectedRevisionId.Should().Be(Alias.RevisionId);
            Moves++;
            Alias = new(version, "rev2", false);
            return Task.CompletedTask;
        }
        public Task<string> PublishImageAsync(string functionName, string imageUri, string? region, string? serviceUrl, int maxAttempts, int delayMilliseconds, CancellationToken cancellationToken = default)
        {
            imageUri.Should().Be(Image);
            PublishCount++;
            if (ChangeAliasOnPublish) Alias = new("42", "other-revision", false);
            return Task.FromResult("43");
        }
    }

    private sealed class FakeAliasClient : IAwsLambdaAliasClient
    {
        public Task<AwsLambdaAliasState> GetAliasAsync(string functionName, string aliasName, string? region, string? serviceUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AwsLambdaAliasState> UpdateAliasAsync(string functionName, string aliasName, string functionVersion, IReadOnlyDictionary<string, double>? additionalVersionWeights, string? region, string? serviceUrl = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
