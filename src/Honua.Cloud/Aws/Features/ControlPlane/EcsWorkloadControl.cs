// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon;
using Amazon.ECS;
using Amazon.ECS.Model;

namespace Honua.ControlPlane;

/// <summary>ECS service identity read back around a governed restart or desired-count change.</summary>
internal sealed record EcsWorkloadSnapshot
{
    public required string TaskDefinitionArn { get; init; }

    public int DesiredCount { get; init; }
}

/// <summary>
/// Narrow ECS writes for governed workload actions. Task-definition rollout stays on the
/// deploy backend. These calls never register a task definition or change an image.
/// </summary>
internal interface IEcsWorkloadControl
{
    Task<EcsWorkloadSnapshot> DescribeAsync(
        string cluster,
        string serviceName,
        string? region,
        CancellationToken cancellationToken = default);

    Task<bool> HasImmutableImagesAsync(string taskDefinitionArn, string? region, CancellationToken cancellationToken = default);

    System.Threading.Tasks.Task ForceNewDeploymentAsync(
        string cluster,
        string serviceName,
        string? region,
        CancellationToken cancellationToken = default);

    Task<EcsWorkloadSnapshot> SetDesiredCountAsync(
        string cluster,
        string serviceName,
        int desiredCount,
        string? region,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// UpdateService bodies for governed ECS actions. Each request sets one behavior and
/// leaves task definition, network, and every other field unset.
/// </summary>
internal static class EcsWorkloadUpdate
{
    public static UpdateServiceRequest ForceNewDeployment(string cluster, string serviceName)
        => new()
        {
            Cluster = cluster,
            Service = serviceName,
            ForceNewDeployment = true
        };

    public static UpdateServiceRequest DesiredCount(string cluster, string serviceName, int desiredCount)
        => new()
        {
            Cluster = cluster,
            Service = serviceName,
            DesiredCount = desiredCount
        };
}

/// <summary>Process-credential ECS client for governed restart and desired-count changes.</summary>
internal sealed class AwsSdkEcsWorkloadControl : IEcsWorkloadControl
{
    public async Task<EcsWorkloadSnapshot> DescribeAsync(
        string cluster,
        string serviceName,
        string? region,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region);
        var response = await client.DescribeServicesAsync(
                new DescribeServicesRequest
                {
                    Cluster = cluster,
                    Services = [serviceName]
                },
                cancellationToken)
            .ConfigureAwait(false);
        var service = response.Services?.FirstOrDefault();
        if (response.Failures is { Count: > 0 } || service is null || string.IsNullOrWhiteSpace(service.TaskDefinition))
        {
            throw new InvalidOperationException("ECS service was not found.");
        }

        return new EcsWorkloadSnapshot
        {
            TaskDefinitionArn = service.TaskDefinition,
            DesiredCount = service.DesiredCount ?? 0
        };
    }

    public async Task<bool> HasImmutableImagesAsync(string taskDefinitionArn, string? region, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region);
        var response = await client.DescribeTaskDefinitionAsync(
            new DescribeTaskDefinitionRequest { TaskDefinition = taskDefinitionArn }, cancellationToken).ConfigureAwait(false);
        var containers = response.TaskDefinition?.ContainerDefinitions;
        return containers is { Count: > 0 } && containers.All(container => IsImmutableImage(container.Image));
    }

    internal static bool IsImmutableImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return false;
        var index = image.LastIndexOf("@sha256:", StringComparison.Ordinal);
        if (index <= 0 || image.Length != index + 8 + 64) return false;
        return image.AsSpan(index + 8).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    public async System.Threading.Tasks.Task ForceNewDeploymentAsync(
        string cluster,
        string serviceName,
        string? region,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region);
        var response = await client.UpdateServiceAsync(
                EcsWorkloadUpdate.ForceNewDeployment(cluster, serviceName),
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Service is null)
        {
            throw new InvalidOperationException("ECS workload update failed.");
        }
    }

    public async Task<EcsWorkloadSnapshot> SetDesiredCountAsync(
        string cluster,
        string serviceName,
        int desiredCount,
        string? region,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region);
        var response = await client.UpdateServiceAsync(
                EcsWorkloadUpdate.DesiredCount(cluster, serviceName, desiredCount),
                cancellationToken)
            .ConfigureAwait(false);
        var service = response.Service;
        if (service is null || string.IsNullOrWhiteSpace(service.TaskDefinition))
        {
            throw new InvalidOperationException("ECS workload update failed.");
        }

        return new EcsWorkloadSnapshot
        {
            TaskDefinitionArn = service.TaskDefinition,
            DesiredCount = service.DesiredCount ?? desiredCount
        };
    }

    private static AmazonECSClient CreateClient(string? region)
        => string.IsNullOrWhiteSpace(region)
            ? new AmazonECSClient()
            : new AmazonECSClient(RegionEndpoint.GetBySystemName(region));
}
