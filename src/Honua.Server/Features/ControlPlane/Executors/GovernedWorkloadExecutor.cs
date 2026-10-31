// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane.Executors;

/// <summary>
/// Product executor for governed ECS and Lambda workload actions. Credentials are the
/// process chain of the deployment that hosts this server. A missing registration fails
/// closed before any provider call.
/// </summary>
internal interface IGovernedWorkloadExecutor
{
    Task RestartEcsAsync(
        string cluster,
        string serviceName,
        string? region,
        CancellationToken cancellationToken = default);

    Task SetEcsDesiredCountAsync(
        string cluster,
        string serviceName,
        int desiredCount,
        string? region,
        CancellationToken cancellationToken = default);

    Task RestartLambdaAliasAsync(
        string functionName,
        string aliasName,
        string? region,
        string? serviceUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>Stable refusal codes for governed workload actions. The exception message is the code.</summary>
internal static class WorkloadExecutorCodes
{
    public const string Unconfigured = "workload-executor-unconfigured";

    public const string TargetNotRegistered = "workload-target-not-registered";

    public const string TargetKindMismatch = "workload-target-kind-mismatch";

    public const string ConfigFieldRejected = "workload-config-field-rejected";

    public const string ConfigValueRejected = "workload-config-value-rejected";

    public const string TargetMissing = "workload-target-missing";

    public const string FingerprintChanged = "workload-config-fingerprint-changed";

    public const string ProviderFailed = "workload-provider-failed";
}
