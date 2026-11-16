// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.CloudIntegration.Tests;

public sealed class RealAwsCertificationFixture : IAsyncLifetime
{
    public const string EnabledEnvVar = "HONUA_REALAWS_CERT_ENABLED";

    public const string RegionEnvVar = "HONUA_REALAWS_CERT_REGION";

    public const string JobQueueArnEnvVar = "HONUA_REALAWS_CERT_JOB_QUEUE_ARN";

    public const string JobDefinitionArnSmallEnvVar = "HONUA_REALAWS_CERT_JOBDEF_ARN_S";

    public const string JobDefinitionArnMediumEnvVar = "HONUA_REALAWS_CERT_JOBDEF_ARN_M";

    public const string JobDefinitionArnLargeEnvVar = "HONUA_REALAWS_CERT_JOBDEF_ARN_L";

    public const string JobDefinitionArnExtraLargeEnvVar = "HONUA_REALAWS_CERT_JOBDEF_ARN_XL";

    public const string JobRoleArnEnvVar = "HONUA_REALAWS_CERT_JOB_ROLE_ARN";

    public const string ExecutionRoleArnEnvVar = "HONUA_REALAWS_CERT_EXECUTION_ROLE_ARN";

    public const string ArtifactBucketEnvVar = "HONUA_REALAWS_CERT_ARTIFACT_BUCKET";

    public const string LambdaFunctionEnvVar = "HONUA_REALAWS_CERT_LAMBDA_FUNCTION";

    public const string LambdaAliasEnvVar = "HONUA_REALAWS_CERT_LAMBDA_ALIAS";

    public const string EcsClusterEnvVar = "HONUA_REALAWS_CERT_ECS_CLUSTER";

    public const string EcsServiceEnvVar = "HONUA_REALAWS_CERT_ECS_SERVICE";

    public const string AlbListenerArnEnvVar = "HONUA_REALAWS_CERT_ALB_LISTENER_ARN";

    public const string CanaryTargetGroupArnEnvVar = "HONUA_REALAWS_CERT_CANARY_TARGET_GROUP_ARN";

    public const string StableTargetGroupArnEnvVar = "HONUA_REALAWS_CERT_STABLE_TARGET_GROUP_ARN";

    public const string RunTagKey = "honua-cert-run";

    public const string CreatedTagKey = "honua-cert-created";

    public bool Enabled { get; private set; }

    public string Region { get; private set; } = DefaultRegion;

    public const string DefaultRegion = "us-east-1";

    public string RunId { get; private set; } = "unset0000";

    public string ResourcePrefix { get; private set; } = "honua-cert-unset";

    public string JobDefinitionRunPrefix { get; private set; } = "honua-certrun-unset";

    public const string JobDefinitionRunNamespace = "honua-certrun";

    public string? JobQueueArn { get; private set; }

    public string? JobDefinitionArnSmall { get; private set; }

    public string? JobDefinitionArnMedium { get; private set; }

    public string? JobDefinitionArnLarge { get; private set; }

    public string? JobDefinitionArnExtraLarge { get; private set; }

    public string? JobRoleArn { get; private set; }

    public string? ExecutionRoleArn { get; private set; }

    public string? ArtifactBucket { get; private set; }

    public string? LambdaFunctionName { get; private set; }

    public string? LambdaAliasName { get; private set; }

    public string? EcsCluster { get; private set; }

    public string? EcsService { get; private set; }

    public string? AlbListenerArn { get; private set; }

    public string? CanaryTargetGroupArn { get; private set; }

    public string? StableTargetGroupArn { get; private set; }

    public bool BatchExecutionConfigured
        => Enabled && !string.IsNullOrWhiteSpace(JobQueueArn) && !string.IsNullOrWhiteSpace(JobDefinitionArnSmall);

    public bool S3ArtifactConfigured
        => Enabled && !string.IsNullOrWhiteSpace(ArtifactBucket);

    public bool LambdaAliasConfigured
        => Enabled && !string.IsNullOrWhiteSpace(LambdaFunctionName) && !string.IsNullOrWhiteSpace(LambdaAliasName);

    public bool EcsAlbConfigured
        => Enabled
            && !string.IsNullOrWhiteSpace(EcsCluster)
            && !string.IsNullOrWhiteSpace(EcsService)
            && !string.IsNullOrWhiteSpace(AlbListenerArn)
            && !string.IsNullOrWhiteSpace(CanaryTargetGroupArn)
            && !string.IsNullOrWhiteSpace(StableTargetGroupArn);

    public IReadOnlyDictionary<string, string> RunTags()
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RunTagKey] = RunId,
            [CreatedTagKey] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    public Task InitializeAsync()
    {
        var enabledRaw = Environment.GetEnvironmentVariable(EnabledEnvVar);
        Enabled = string.Equals(enabledRaw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        Region =
            FirstNonEmpty(
                Environment.GetEnvironmentVariable(RegionEnvVar),
                Environment.GetEnvironmentVariable("AWS_REGION"),
                Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"))
            ?? DefaultRegion;

        RunId = Guid.NewGuid().ToString("N")[..8];
        ResourcePrefix = $"honua-cert-{RunId}";
        JobDefinitionRunPrefix = $"{JobDefinitionRunNamespace}-{RunId}";

        JobQueueArn = Read(JobQueueArnEnvVar);
        JobDefinitionArnSmall = Read(JobDefinitionArnSmallEnvVar);
        JobDefinitionArnMedium = Read(JobDefinitionArnMediumEnvVar);
        JobDefinitionArnLarge = Read(JobDefinitionArnLargeEnvVar);
        JobDefinitionArnExtraLarge = Read(JobDefinitionArnExtraLargeEnvVar);
        JobRoleArn = Read(JobRoleArnEnvVar);
        ExecutionRoleArn = Read(ExecutionRoleArnEnvVar);
        ArtifactBucket = Read(ArtifactBucketEnvVar);
        LambdaFunctionName = Read(LambdaFunctionEnvVar);
        LambdaAliasName = Read(LambdaAliasEnvVar);
        EcsCluster = Read(EcsClusterEnvVar);
        EcsService = Read(EcsServiceEnvVar);
        AlbListenerArn = Read(AlbListenerArnEnvVar);
        CanaryTargetGroupArn = Read(CanaryTargetGroupArnEnvVar);
        StableTargetGroupArn = Read(StableTargetGroupArnEnvVar);

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string? Read(string envVar)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim();
}
