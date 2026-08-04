// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Server.Features.Collaboration;

/// <summary>
/// Deployment-topology signal for the saved-map collaboration surface (honua-server#2999).
/// Answers one question: can more than one instance of this server accept edits for the same
/// saved map?
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately NOT inferred from the presence of a Redis multiplexer. Redis is a
/// perfectly ordinary dependency for a SINGLE instance (cache, durable jobs, rate limiting), so
/// treating "Redis is configured" as "multi-replica" would fail-close live co-editing for the
/// most common production shape. Topology is an operator declaration instead: the existing
/// <c>Deployment:Mode</c> switch (<c>MultiNode</c>) that already gates the platform's other
/// scale-out requirements, with <c>Collaboration:MultiReplica</c> as a direct override for
/// deployments that scale out without setting the deployment mode.
/// </para>
/// <para>
/// Default is single-instance, matching <c>Deployment:Mode</c>'s own default. Operators who
/// scale out must declare it — the same contract the rest of the platform's multi-node
/// requirements (shared file storage, Redis) already rely on.
/// </para>
/// </remarks>
internal sealed class SavedMapCollaborationTopology
{
    /// <summary>Direct override key: <c>Collaboration:MultiReplica</c>.</summary>
    public const string MultiReplicaConfigurationKey = "Collaboration:MultiReplica";

    /// <summary>Existing platform-wide scale-out declaration: <c>Deployment:Mode</c>.</summary>
    public const string DeploymentModeConfigurationKey = "Deployment:Mode";

    public SavedMapCollaborationTopology(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (bool.TryParse(configuration[MultiReplicaConfigurationKey], out var explicitMultiReplica))
        {
            IsMultiReplica = explicitMultiReplica;
            return;
        }

        IsMultiReplica = string.Equals(
            configuration[DeploymentModeConfigurationKey],
            "MultiNode",
            StringComparison.OrdinalIgnoreCase);
    }

    private SavedMapCollaborationTopology(bool isMultiReplica) => IsMultiReplica = isMultiReplica;

    /// <summary>
    /// Whether more than one server instance may accept edits for the same saved map, so
    /// process-local collaboration state cannot be authoritative.
    /// </summary>
    public bool IsMultiReplica { get; }

    /// <summary>Creates a topology with an explicit value (tests and composition roots).</summary>
    public static SavedMapCollaborationTopology ForMultiReplica(bool isMultiReplica) => new(isMultiReplica);
}
