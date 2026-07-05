// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Gate + configuration surface for the real-AWS certification lane (#2164). Unlike the emulated
/// fixtures, this one targets a LIVE AWS account, so it is OFF by default and only reports
/// <see cref="Enabled"/> = true when <c>HONUA_REALAWS_CERT_ENABLED</c> is explicitly set to
/// <c>true</c>. The dedicated <c>real-aws-certification.yml</c> workflow sets that flag (and
/// assumes an OIDC role) only when the maintainer AWS credentials are present; on forks, PRs
/// without secrets, and ordinary local runs the flag is absent so every certification test
/// <c>[SkippableFact]</c>-skips rather than failing or spending money.
///
/// Credentials are resolved by the AWS SDK default chain (OIDC web-identity in CI, the shared
/// <c>~/.aws</c> profile locally) — this fixture deliberately does not handle secrets itself. It
/// only surfaces the master switch, the target <see cref="Region"/>, a per-run unique
/// <see cref="RunId"/> / <see cref="ResourcePrefix"/> so every resource a certification test
/// creates is namespaced <c>honua-cert-*</c> and can never collide with or clobber pre-existing
/// account infrastructure, and the ARNs/names of the standing certification stack provisioned by
/// <c>honua-iac</c> (<c>examples/aws-cert</c>).
///
/// STACK-OUTPUT → ENV-VAR CONTRACT (honua-iac <c>examples/aws-cert</c> outputs → repo variable →
/// env var this fixture reads). Every input is OPTIONAL: each certification cell <c>Skip</c>s
/// cleanly when its specific inputs are absent, so the lane degrades cell-by-cell rather than
/// failing wholesale.
/// <list type="table">
///   <item><term>gp_job_queue_arn</term><description><c>HONUA_REALAWS_CERT_JOB_QUEUE_ARN</c></description></item>
///   <item><term>gp_job_definition_arns.s</term><description><c>HONUA_REALAWS_CERT_JOBDEF_ARN_S</c> (required for the Batch execution cell)</description></item>
///   <item><term>gp_job_definition_arns.{m,l,xl}</term><description><c>HONUA_REALAWS_CERT_JOBDEF_ARN_M/_L/_XL</c> (optional)</description></item>
///   <item><term>gp_job_role_arn</term><description><c>HONUA_REALAWS_CERT_JOB_ROLE_ARN</c></description></item>
///   <item><term>gp_execution_role_arn</term><description><c>HONUA_REALAWS_CERT_EXECUTION_ROLE_ARN</c></description></item>
///   <item><term>cert_artifact_bucket</term><description><c>HONUA_REALAWS_CERT_ARTIFACT_BUCKET</c> (required for the S3 cell)</description></item>
///   <item><term>(cert stack Lambda)</term><description><c>HONUA_REALAWS_CERT_LAMBDA_FUNCTION</c> + <c>HONUA_REALAWS_CERT_LAMBDA_ALIAS</c> (required for the Lambda cell)</description></item>
///   <item><term>(cert stack ECS/ALB)</term><description><c>HONUA_REALAWS_CERT_ECS_CLUSTER</c>, <c>HONUA_REALAWS_CERT_ECS_SERVICE</c>, <c>HONUA_REALAWS_CERT_ALB_LISTENER_ARN</c>, <c>HONUA_REALAWS_CERT_CANARY_TARGET_GROUP_ARN</c>, <c>HONUA_REALAWS_CERT_STABLE_TARGET_GROUP_ARN</c> (required for the ECS/ALB weighted-cutover cell)</description></item>
/// </list>
/// </summary>
public sealed class RealAwsCertificationFixture : IAsyncLifetime
{
    /// <summary>Master switch. The lane runs only when this is set to <c>true</c>.</summary>
    public const string EnabledEnvVar = "HONUA_REALAWS_CERT_ENABLED";

    /// <summary>Optional region override for the certification lane.</summary>
    public const string RegionEnvVar = "HONUA_REALAWS_CERT_REGION";

    /// <summary><c>gp_job_queue_arn</c> stack output — the Batch job queue the execution cell submits to.</summary>
    public const string JobQueueArnEnvVar = "HONUA_REALAWS_CERT_JOB_QUEUE_ARN";

    /// <summary><c>gp_job_definition_arns.s</c> stack output — smallest-tier Batch job definition.</summary>
    public const string JobDefinitionArnSmallEnvVar = "HONUA_REALAWS_CERT_JOBDEF_ARN_S";

    /// <summary><c>gp_job_definition_arns.m</c> stack output (optional).</summary>
    public const string JobDefinitionArnMediumEnvVar = "HONUA_REALAWS_CERT_JOBDEF_ARN_M";

    /// <summary><c>gp_job_definition_arns.l</c> stack output (optional).</summary>
    public const string JobDefinitionArnLargeEnvVar = "HONUA_REALAWS_CERT_JOBDEF_ARN_L";

    /// <summary><c>gp_job_definition_arns.xl</c> stack output (optional).</summary>
    public const string JobDefinitionArnExtraLargeEnvVar = "HONUA_REALAWS_CERT_JOBDEF_ARN_XL";

    /// <summary><c>gp_job_role_arn</c> stack output — the Batch container task role.</summary>
    public const string JobRoleArnEnvVar = "HONUA_REALAWS_CERT_JOB_ROLE_ARN";

    /// <summary><c>gp_execution_role_arn</c> stack output — the Fargate execution role.</summary>
    public const string ExecutionRoleArnEnvVar = "HONUA_REALAWS_CERT_EXECUTION_ROLE_ARN";

    /// <summary><c>cert_artifact_bucket</c> stack output — the S3 bucket the artifact cell round-trips.</summary>
    public const string ArtifactBucketEnvVar = "HONUA_REALAWS_CERT_ARTIFACT_BUCKET";

    /// <summary>Cert-stack Lambda function name/ARN driven by the Lambda alias-flip cell.</summary>
    public const string LambdaFunctionEnvVar = "HONUA_REALAWS_CERT_LAMBDA_FUNCTION";

    /// <summary>Cert-stack Lambda alias name the alias-flip cell reads, flips, and restores.</summary>
    public const string LambdaAliasEnvVar = "HONUA_REALAWS_CERT_LAMBDA_ALIAS";

    /// <summary>Cert-stack ECS cluster the ECS/ALB weighted-cutover cell drives.</summary>
    public const string EcsClusterEnvVar = "HONUA_REALAWS_CERT_ECS_CLUSTER";

    /// <summary>Cert-stack ECS service the ECS/ALB weighted-cutover cell drives.</summary>
    public const string EcsServiceEnvVar = "HONUA_REALAWS_CERT_ECS_SERVICE";

    /// <summary>
    /// Cert-stack ALB listener ARN. The ECS/ALB weighted-cutover cell resolves this listener's
    /// DEFAULT rule (the weighted forward action the backend mutates) at test start.
    /// </summary>
    public const string AlbListenerArnEnvVar = "HONUA_REALAWS_CERT_ALB_LISTENER_ARN";

    /// <summary>Cert-stack canary target group ARN the ECS/ALB weighted-cutover cell shifts weight to.</summary>
    public const string CanaryTargetGroupArnEnvVar = "HONUA_REALAWS_CERT_CANARY_TARGET_GROUP_ARN";

    /// <summary>Cert-stack stable target group ARN the ECS/ALB weighted-cutover cell shifts weight to.</summary>
    public const string StableTargetGroupArnEnvVar = "HONUA_REALAWS_CERT_STABLE_TARGET_GROUP_ARN";

    /// <summary>
    /// Tag key applied to every certification-created resource (where the AWS API supports tags)
    /// carrying the per-run <see cref="RunId"/>. The reaper (<see cref="CertificationResourceReaper"/>)
    /// keys STRICTLY off this tag so it can never touch the standing certification-stack resources,
    /// which do not carry it — even though they share the <c>honua-cert-*</c> name prefix.
    /// </summary>
    public const string RunTagKey = "honua-cert-run";

    /// <summary>
    /// Tag key carrying the resource's creation time as Unix seconds. The reaper uses it as the TTL
    /// clock for resources (like Batch job definitions) whose describe responses expose no native
    /// creation timestamp.
    /// </summary>
    public const string CreatedTagKey = "honua-cert-created";

    /// <summary>
    /// True only when the lane is explicitly enabled. Defaults to false so the certification
    /// tests skip everywhere except the gated workflow / an opted-in local run.
    /// </summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// Target AWS region. Resolves <c>HONUA_REALAWS_CERT_REGION</c> → <c>AWS_REGION</c> →
    /// <c>AWS_DEFAULT_REGION</c> → <c>us-east-1</c> (the honua-iac <c>examples/aws-cert</c> default).
    /// </summary>
    public string Region { get; private set; } = DefaultRegion;

    /// <summary>Default region, aligned with the honua-iac <c>examples/aws-cert</c> stack default.</summary>
    public const string DefaultRegion = "us-east-1";

    /// <summary>
    /// Per-run unique 8-hex id. Every created resource is namespaced/tagged with it so a
    /// certification run is isolated, traceable, and reap-able, and can never be confused with
    /// existing infrastructure.
    /// </summary>
    public string RunId { get; private set; } = "unset0000";

    /// <summary>
    /// Per-run unique prefix (<c>honua-cert-{RunId}</c>) applied to every created resource so a
    /// certification run is isolated and traceable, and can never be confused with existing infra.
    /// </summary>
    public string ResourcePrefix { get; private set; } = "honua-cert-unset";

    /// <summary><c>gp_job_queue_arn</c> — null when unconfigured.</summary>
    public string? JobQueueArn { get; private set; }

    /// <summary><c>gp_job_definition_arns.s</c> — null when unconfigured.</summary>
    public string? JobDefinitionArnSmall { get; private set; }

    /// <summary><c>gp_job_definition_arns.m</c> — null when unconfigured.</summary>
    public string? JobDefinitionArnMedium { get; private set; }

    /// <summary><c>gp_job_definition_arns.l</c> — null when unconfigured.</summary>
    public string? JobDefinitionArnLarge { get; private set; }

    /// <summary><c>gp_job_definition_arns.xl</c> — null when unconfigured.</summary>
    public string? JobDefinitionArnExtraLarge { get; private set; }

    /// <summary><c>gp_job_role_arn</c> — null when unconfigured.</summary>
    public string? JobRoleArn { get; private set; }

    /// <summary><c>gp_execution_role_arn</c> — null when unconfigured.</summary>
    public string? ExecutionRoleArn { get; private set; }

    /// <summary><c>cert_artifact_bucket</c> — null when unconfigured.</summary>
    public string? ArtifactBucket { get; private set; }

    /// <summary>Cert-stack Lambda function name/ARN — null when unconfigured.</summary>
    public string? LambdaFunctionName { get; private set; }

    /// <summary>Cert-stack Lambda alias name — null when unconfigured.</summary>
    public string? LambdaAliasName { get; private set; }

    /// <summary>Cert-stack ECS cluster — null when unconfigured.</summary>
    public string? EcsCluster { get; private set; }

    /// <summary>Cert-stack ECS service — null when unconfigured.</summary>
    public string? EcsService { get; private set; }

    /// <summary>Cert-stack ALB listener ARN — null when unconfigured.</summary>
    public string? AlbListenerArn { get; private set; }

    /// <summary>Cert-stack canary target group ARN — null when unconfigured.</summary>
    public string? CanaryTargetGroupArn { get; private set; }

    /// <summary>Cert-stack stable target group ARN — null when unconfigured.</summary>
    public string? StableTargetGroupArn { get; private set; }

    /// <summary>
    /// True when the lane is enabled AND the Batch execution cell's required inputs (job queue +
    /// smallest-tier job definition) are present.
    /// </summary>
    public bool BatchExecutionConfigured
        => Enabled && !string.IsNullOrWhiteSpace(JobQueueArn) && !string.IsNullOrWhiteSpace(JobDefinitionArnSmall);

    /// <summary>True when the lane is enabled AND the artifact bucket is present.</summary>
    public bool S3ArtifactConfigured
        => Enabled && !string.IsNullOrWhiteSpace(ArtifactBucket);

    /// <summary>True when the lane is enabled AND both the Lambda function and alias are present.</summary>
    public bool LambdaAliasConfigured
        => Enabled && !string.IsNullOrWhiteSpace(LambdaFunctionName) && !string.IsNullOrWhiteSpace(LambdaAliasName);

    /// <summary>
    /// True when the lane is enabled AND every ECS/ALB weighted-cutover input is present: the ECS
    /// cluster + service, the ALB listener whose default rule is mutated, and both the canary and
    /// stable target group ARNs. The cell resolves the listener's default rule ARN from
    /// <see cref="AlbListenerArn"/> at test start, so no separate rule-ARN input is required.
    /// </summary>
    public bool EcsAlbConfigured
        => Enabled
            && !string.IsNullOrWhiteSpace(EcsCluster)
            && !string.IsNullOrWhiteSpace(EcsService)
            && !string.IsNullOrWhiteSpace(AlbListenerArn)
            && !string.IsNullOrWhiteSpace(CanaryTargetGroupArn)
            && !string.IsNullOrWhiteSpace(StableTargetGroupArn);

    /// <summary>
    /// The per-run resource-tag set: <see cref="RunTagKey"/>=<see cref="RunId"/> plus a
    /// <see cref="CreatedTagKey"/> creation stamp. Applied wherever the AWS API supports tags so the
    /// reaper can find and expire leaked resources without ever matching the standing stack.
    /// </summary>
    public IReadOnlyDictionary<string, string> RunTags()
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RunTagKey] = RunId,
            [CreatedTagKey] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>Resolves the lane configuration from the environment.</summary>
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

    /// <summary>No standing resources; certification tests own their own create/teardown.</summary>
    public Task DisposeAsync() => Task.CompletedTask;

    private static string? Read(string envVar)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}
