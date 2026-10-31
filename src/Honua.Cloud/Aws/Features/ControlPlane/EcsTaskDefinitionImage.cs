// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.ECS.Model;

namespace Honua.ControlPlane;

/// <summary>
/// Clones an ECS task definition so a release image rollout changes one container image
/// and keeps every other field. Registering that clone is how a digest becomes a revision
/// ECS can run. Dropping secrets, environment, or log configuration would change the service.
/// </summary>
internal static class EcsTaskDefinitionImage
{
    /// <summary>
    /// Builds a register request from <paramref name="source"/> with <paramref name="containerName"/>
    /// set to <paramref name="image"/>. Throws <see cref="InvalidOperationException"/> when the
    /// container is missing or ambiguous, before any register call.
    /// </summary>
    public static RegisterTaskDefinitionRequest CloneWithImage(
        TaskDefinition source,
        string containerName,
        string image)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        if (!ReleaseImageDigest.IsImmutable(image))
        {
            throw new InvalidOperationException(
                $"Refusing to register a task definition from '{ReleaseImageDigest.NotImmutableCode}'.");
        }

        var containers = source.ContainerDefinitions ?? [];
        var matches = 0;
        foreach (var container in containers)
        {
            if (string.Equals(container.Name, containerName, StringComparison.Ordinal))
            {
                matches++;
            }
        }

        if (matches != 1)
        {
            throw new InvalidOperationException(
                matches == 0
                    ? $"Task definition '{source.Family}' has no container named '{containerName}'."
                    : $"Task definition '{source.Family}' has more than one container named '{containerName}'.");
        }

        if (string.IsNullOrWhiteSpace(source.Family))
        {
            throw new InvalidOperationException("The current task definition has no family, so a new revision cannot be registered.");
        }

        var cloned = new List<ContainerDefinition>(containers.Count);
        foreach (var container in containers)
        {
            var replacement = string.Equals(container.Name, containerName, StringComparison.Ordinal)
                ? image
                : null;
            cloned.Add(CopyContainer(container, replacement));
        }

        return new RegisterTaskDefinitionRequest
        {
            Family = source.Family,
            TaskRoleArn = source.TaskRoleArn,
            ExecutionRoleArn = source.ExecutionRoleArn,
            NetworkMode = source.NetworkMode,
            ContainerDefinitions = cloned,
            Volumes = source.Volumes,
            PlacementConstraints = source.PlacementConstraints,
            RequiresCompatibilities = source.RequiresCompatibilities,
            Cpu = source.Cpu,
            Memory = source.Memory,
            PidMode = source.PidMode,
            IpcMode = source.IpcMode,
            ProxyConfiguration = source.ProxyConfiguration,
            InferenceAccelerators = source.InferenceAccelerators,
            EphemeralStorage = source.EphemeralStorage,
            RuntimePlatform = source.RuntimePlatform,
            EnableFaultInjection = source.EnableFaultInjection,
        };
    }

    /// <summary>
    /// Picks the container whose image a release may replace. An explicit name wins.
    /// Otherwise the container named <c>honua</c>, otherwise the only container.
    /// Anything else fails closed.
    /// </summary>
    public static string? SelectContainerName(IReadOnlyList<string> containerNames, string? requestedName)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            return containerNames.Count(name => string.Equals(name, requestedName, StringComparison.Ordinal)) == 1
                ? requestedName
                : null;
        }

        if (containerNames.Count(name => string.Equals(name, "honua", StringComparison.Ordinal)) == 1)
        {
            return "honua";
        }

        return containerNames.Count == 1 ? containerNames[0] : null;
    }

    private static ContainerDefinition CopyContainer(ContainerDefinition source, string? replacementImage)
        => new()
        {
            Command = source.Command,
            Cpu = source.Cpu,
            CredentialSpecs = source.CredentialSpecs,
            DependsOn = source.DependsOn,
            DisableNetworking = source.DisableNetworking,
            DnsSearchDomains = source.DnsSearchDomains,
            DnsServers = source.DnsServers,
            DockerLabels = source.DockerLabels,
            DockerSecurityOptions = source.DockerSecurityOptions,
            EntryPoint = source.EntryPoint,
            Environment = source.Environment,
            EnvironmentFiles = source.EnvironmentFiles,
            Essential = source.Essential,
            ExtraHosts = source.ExtraHosts,
            FirelensConfiguration = source.FirelensConfiguration,
            HealthCheck = source.HealthCheck,
            Hostname = source.Hostname,
            Image = replacementImage ?? source.Image,
            Interactive = source.Interactive,
            Links = source.Links,
            LinuxParameters = source.LinuxParameters,
            LogConfiguration = source.LogConfiguration,
            Memory = source.Memory,
            MemoryReservation = source.MemoryReservation,
            MountPoints = source.MountPoints,
            Name = source.Name,
            PortMappings = source.PortMappings,
            Privileged = source.Privileged,
            PseudoTerminal = source.PseudoTerminal,
            ReadonlyRootFilesystem = source.ReadonlyRootFilesystem,
            RepositoryCredentials = source.RepositoryCredentials,
            ResourceRequirements = source.ResourceRequirements,
            RestartPolicy = source.RestartPolicy,
            Secrets = source.Secrets,
            StartTimeout = source.StartTimeout,
            StopTimeout = source.StopTimeout,
            SystemControls = source.SystemControls,
            Ulimits = source.Ulimits,
            User = source.User,
            VersionConsistency = source.VersionConsistency,
            VolumesFrom = source.VolumesFrom,
            WorkingDirectory = source.WorkingDirectory,
        };
}
