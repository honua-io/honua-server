// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

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
                Parameters = new Dictionary<string, string>(target.Parameters, StringComparer.Ordinal)
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
                Parameters = new Dictionary<string, string>(definition.Parameters, StringComparer.Ordinal)
            })
            .ToArray();
}
