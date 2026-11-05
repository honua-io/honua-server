// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Server.Features.Collaboration;

// Multi-replica is an operator declaration, not the presence of Redis, so Redis on a single instance does not fail-close live editing.
internal sealed class SavedMapCollaborationTopology
{
    public const string MultiReplicaConfigurationKey = "Collaboration:MultiReplica";

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

    public bool IsMultiReplica { get; }

    public static SavedMapCollaborationTopology ForMultiReplica(bool isMultiReplica) => new(isMultiReplica);
}
