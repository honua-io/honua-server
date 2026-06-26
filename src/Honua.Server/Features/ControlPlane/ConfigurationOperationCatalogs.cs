// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>
/// Configuration-backed deploy target registry for control-plane workflows.
/// </summary>
internal sealed class ConfigurationDeployTargetRegistry(IOptionsMonitor<ControlPlaneOptions> options) : IDeployTargetRegistry
{
    public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>(BuildTargets(options.CurrentValue));

    public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var target = BuildTargets(options.CurrentValue)
            .FirstOrDefault(candidate => string.Equals(candidate.TargetId, targetId, StringComparison.Ordinal));

        return Task.FromResult(target);
    }

    private static DeployTargetDefinition[] BuildTargets(ControlPlaneOptions options)
        => options.DeployTargets
            .Where(target => !string.IsNullOrWhiteSpace(target.TargetId))
            .Select(target => new DeployTargetDefinition
            {
                TargetId = target.TargetId,
                TargetKind = target.TargetKind,
                Backend = target.Backend,
                Environment = target.Environment,
                TargetName = target.TargetName,
                ArtifactReference = target.ArtifactReference,
                RuntimeProfile = target.RuntimeProfile,
                RequiresApproval = target.RequiresApproval,
                RequiresOutOfBandMigrations = target.RequiresOutOfBandMigrations,
                Parameters = ConfigurationOperationCatalogHelpers.BuildParameters(target.Parameters, target.ParameterEntries)
            })
            .ToArray();
}

/// <summary>
/// Configuration-backed workload catalog for specialized compute jobs.
/// </summary>
internal sealed class ConfigurationExecutionJobDefinitionRegistry(IOptionsMonitor<ControlPlaneOptions> options)
    : IExecutionJobDefinitionRegistry
{
    public Task<IReadOnlyList<ExecutionJobDefinition>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ExecutionJobDefinition>>(BuildDefinitions(options.CurrentValue));

    public Task<ExecutionJobDefinition?> GetAsync(string workloadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);

        var definition = BuildDefinitions(options.CurrentValue)
            .FirstOrDefault(candidate => string.Equals(candidate.WorkloadId, workloadId, StringComparison.Ordinal));

        return Task.FromResult(definition);
    }

    private static ExecutionJobDefinition[] BuildDefinitions(ControlPlaneOptions options)
        => options.ExecutionWorkloads
            .Where(definition => !string.IsNullOrWhiteSpace(definition.WorkloadId))
            .Select(definition => new ExecutionJobDefinition
            {
                WorkloadId = definition.WorkloadId,
                TargetKind = definition.TargetKind,
                Backend = definition.Backend,
                Kind = definition.Kind,
                WorkloadName = definition.WorkloadName,
                ArtifactReference = definition.ArtifactReference,
                RuntimeProfile = definition.RuntimeProfile,
                Parameters = ConfigurationOperationCatalogHelpers.BuildParameters(definition.Parameters, definition.ParameterEntries)
            })
            .Where(ExecutionWorkloadGate.IsActivatable)
            .ToArray();
}

/// <summary>
/// Activation gate for configuration-declared execution workloads.
/// </summary>
/// <remarks>
/// An AWS Batch workload is only usable once the operator supplies the substrate
/// ARNs the <c>AwsBatchComputeBackend</c> requires at submit time: the job-queue ARN
/// plus at least one job-definition ARN (either the single
/// <c>batch.job_definition_arn</c> or one of the per-tier
/// <c>batch.job_definition_arn.{s,m,l,xl}</c> keys). A workload row committed with
/// empty placeholder ARNs (so the entry is discoverable in base appsettings) is
/// therefore dropped here until those parameters are provided through an environment
/// or appsettings override — which means submit routing cleanly falls back to the
/// local/Kubernetes baseline workload and existing deploys (and tests) are unaffected.
/// The parameter keys are matched as literal contract strings rather than via
/// <c>AwsBatchParameterKeys</c> so this gate does not take a hard dependency on the
/// optionally-excluded <c>Honua.Aws</c> assembly (HonuaIncludeAws / HONUA_EXCLUDE_AWS).
/// </remarks>
internal static class ExecutionWorkloadGate
{
    private const string JobQueueArnKey = "batch.job_queue_arn";
    private const string JobDefinitionArnKey = "batch.job_definition_arn";
    private const string JobDefinitionArnTierPrefix = JobDefinitionArnKey + ".";

    public static bool IsActivatable(ExecutionJobDefinition definition)
    {
        if (definition.TargetKind != BatchComputeTargetKind.AwsBatch)
        {
            return true;
        }

        return HasValue(definition.Parameters, JobQueueArnKey)
            && HasAnyJobDefinitionArn(definition.Parameters);
    }

    private static bool HasAnyJobDefinitionArn(IReadOnlyDictionary<string, string> parameters)
    {
        if (HasValue(parameters, JobDefinitionArnKey))
        {
            return true;
        }

        foreach (var entry in parameters)
        {
            if (entry.Key.StartsWith(JobDefinitionArnTierPrefix, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(entry.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasValue(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
}

internal static class ConfigurationOperationCatalogHelpers
{
    public static Dictionary<string, string> BuildParameters(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<ConfigurationParameterEntryOptions> entries)
    {
        var merged = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            merged[entry.Key.Trim()] = entry.Value?.Trim() ?? string.Empty;
        }

        return merged;
    }
}
