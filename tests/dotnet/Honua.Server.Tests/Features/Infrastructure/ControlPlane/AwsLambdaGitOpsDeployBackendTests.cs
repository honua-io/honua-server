// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AwsLambdaGitOpsDeployBackendTests
{
    [Fact]
    public async Task PlanAsync_RejectsLatestAsDesiredRevision()
    {
        var backend = new AwsLambdaGitOpsDeployBackend(
            new StubAwsLambdaAliasClient(),
            NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);

        var plan = await backend.PlanAsync(CreateSpec("$LATEST"));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("$LATEST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_UpdatesAliasToDesiredVersionAndCapturesPreviousRevision()
    {
        var aliasClient = new StubAwsLambdaAliasClient
        {
            CurrentState = new AwsLambdaAliasState
            {
                AliasName = "live",
                AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua:live",
                FunctionVersion = "41"
            }
        };
        var backend = new AwsLambdaGitOpsDeployBackend(aliasClient, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);

        var submission = await backend.StartAsync(CreateOperation(desiredRevision: "42", currentRevision: null));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ObservedRevision.Should().Be("41");
        aliasClient.LastUpdatedVersion.Should().Be("42");
    }

    [Fact]
    public async Task StartAsync_WithCanaryWeight_RoutesPartialTrafficToDesiredVersion()
    {
        var aliasClient = new StubAwsLambdaAliasClient
        {
            CurrentState = new AwsLambdaAliasState
            {
                AliasName = "live",
                AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua:live",
                FunctionVersion = "41"
            }
        };
        var backend = new AwsLambdaGitOpsDeployBackend(aliasClient, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);

        var submission = await backend.StartAsync(CreateOperation(
            desiredRevision: "42",
            currentRevision: null,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lambda.alias_name"] = "live",
                ["target.resource_id"] = "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda",
                ["telemetry.connection"] = "prod-prom",
                ["lambda.canary_weight_percentage"] = "10"
            }));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ObservedRevision.Should().Be("41");
        aliasClient.LastUpdatedVersion.Should().Be("41");
        aliasClient.LastAdditionalVersionWeights.Should().Contain(new KeyValuePair<string, double>("42", 0.10d));
    }

    [Fact]
    public async Task ObserveAsync_ReturnsSucceededWhenAliasPointsToDesiredVersion()
    {
        var aliasClient = new StubAwsLambdaAliasClient
        {
            CurrentState = new AwsLambdaAliasState
            {
                AliasName = "live",
                AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua:live",
                FunctionVersion = "42"
            }
        };
        var backend = new AwsLambdaGitOpsDeployBackend(aliasClient, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);

        var observation = await backend.ObserveAsync(CreateOperation(desiredRevision: "42", currentRevision: "41", status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        observation.ObservedRevision.Should().Be("42");
    }

    [Fact]
    public async Task ObserveAsync_WithCanaryWeight_RecommendsPromotionAfterTelemetry()
    {
        var aliasClient = new StubAwsLambdaAliasClient
        {
            CurrentState = new AwsLambdaAliasState
            {
                AliasName = "live",
                AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua:live",
                FunctionVersion = "41",
                AdditionalVersionWeights = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["42"] = 0.10d
                }
            }
        };
        var backend = new AwsLambdaGitOpsDeployBackend(aliasClient, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);

        var observation = await backend.ObserveAsync(CreateOperation(
            desiredRevision: "42",
            currentRevision: "41",
            status: WorkflowOperationStatus.Reconciling,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lambda.alias_name"] = "live",
                ["target.resource_id"] = "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda",
                ["telemetry.connection"] = "prod-prom",
                ["lambda.canary_weight_percentage"] = "10"
            }));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.PromotionRecommended.Should().BeTrue();
        observation.Message.Should().Contain("10");
    }

    [Fact]
    public async Task PromoteAsync_CompletesWeightedCanaryShift()
    {
        var aliasClient = new StubAwsLambdaAliasClient
        {
            CurrentState = new AwsLambdaAliasState
            {
                AliasName = "live",
                AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua:live",
                FunctionVersion = "41",
                AdditionalVersionWeights = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["42"] = 0.10d
                }
            }
        };
        var backend = new AwsLambdaGitOpsDeployBackend(aliasClient, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);

        var observation = await backend.PromoteAsync(CreateOperation(
            desiredRevision: "42",
            currentRevision: "41",
            status: WorkflowOperationStatus.Reconciling,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lambda.alias_name"] = "live",
                ["target.resource_id"] = "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda",
                ["telemetry.connection"] = "prod-prom",
                ["lambda.canary_weight_percentage"] = "10"
            }));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        observation.ObservedRevision.Should().Be("42");
        aliasClient.LastUpdatedVersion.Should().Be("42");
        aliasClient.LastAdditionalVersionWeights.Should().BeEmpty();
    }

    [Fact]
    public async Task RollbackAsync_UsesCapturedCurrentRevision()
    {
        var aliasClient = new StubAwsLambdaAliasClient
        {
            CurrentState = new AwsLambdaAliasState
            {
                AliasName = "live",
                AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua:live",
                FunctionVersion = "42"
            }
        };
        var backend = new AwsLambdaGitOpsDeployBackend(aliasClient, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);

        var observation = await backend.RollbackAsync(CreateOperation(desiredRevision: "42", currentRevision: "41", status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        observation.ObservedRevision.Should().Be("41");
        aliasClient.LastUpdatedVersion.Should().Be("41");
    }

    private static DeployOperationSpec CreateSpec(
        string desiredRevision,
        IReadOnlyDictionary<string, string>? parameters = null)
        => new()
        {
            TargetId = "prod-lambda",
            TargetKind = DeployTargetKind.AwsLambda,
            Backend = "honua-gitops-aws-lambda",
            Environment = "production",
            TargetName = "honua-prod-lambda",
            ArtifactReference = "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua:sha-42",
            DesiredRevision = desiredRevision,
            RequiresOutOfBandMigrations = true,
            Parameters = parameters ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lambda.alias_name"] = "live",
                ["target.resource_id"] = "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda"
            }
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
                PartitionKey = "production:prod-lambda",
                RequiresExclusiveLease = true
            },
            Deploy = spec
        };
    }

    private sealed class StubAwsLambdaAliasClient : IAwsLambdaAliasClient
    {
        public AwsLambdaAliasState CurrentState { get; set; } = new()
        {
            AliasName = "live",
            AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua:live",
            FunctionVersion = "1"
        };

        public string? LastUpdatedVersion { get; private set; }

        public IReadOnlyDictionary<string, double> LastAdditionalVersionWeights { get; private set; } = new Dictionary<string, double>(StringComparer.Ordinal);

        public Task<AwsLambdaAliasState> GetAliasAsync(
            string functionName,
            string aliasName,
            string? region,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentState);

        public Task<AwsLambdaAliasState> UpdateAliasAsync(
            string functionName,
            string aliasName,
            string functionVersion,
            IReadOnlyDictionary<string, double>? additionalVersionWeights,
            string? region,
            CancellationToken cancellationToken = default)
        {
            LastUpdatedVersion = functionVersion;
            LastAdditionalVersionWeights = additionalVersionWeights is { Count: > 0 }
                ? new Dictionary<string, double>(additionalVersionWeights, StringComparer.Ordinal)
                : new Dictionary<string, double>(StringComparer.Ordinal);
            CurrentState = CurrentState with
            {
                AliasName = aliasName,
                FunctionVersion = functionVersion,
                AdditionalVersionWeights = LastAdditionalVersionWeights
            };

            return Task.FromResult(CurrentState);
        }
    }
}
