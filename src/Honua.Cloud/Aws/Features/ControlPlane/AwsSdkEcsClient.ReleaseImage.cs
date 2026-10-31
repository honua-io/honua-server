// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.ECS;
using Amazon.ECS.Model;

namespace Honua.ControlPlane;

internal sealed partial class AwsSdkEcsClient
{
    public async Task<TaskDefinition> DescribeTaskDefinitionAsync(
        string taskDefinition,
        string? region,
        CancellationToken cancellationToken = default)
    {
        var client = GetClient(region);
        var response = await client.DescribeTaskDefinitionAsync(
                new DescribeTaskDefinitionRequest { TaskDefinition = taskDefinition },
                cancellationToken)
            .ConfigureAwait(false);
        return response.TaskDefinition
            ?? throw new AmazonECSException("ECS task definition was not found.");
    }

    public async Task<string> RegisterTaskDefinitionAsync(
        RegisterTaskDefinitionRequest request,
        string? region,
        CancellationToken cancellationToken = default)
    {
        var client = GetClient(region);
        var response = await client.RegisterTaskDefinitionAsync(request, cancellationToken).ConfigureAwait(false);
        var arn = response.TaskDefinition?.TaskDefinitionArn;
        if (string.IsNullOrWhiteSpace(arn))
        {
            throw new AmazonECSException("RegisterTaskDefinition did not return a task definition ARN.");
        }

        return arn;
    }

    public async Task<string> RunTaskAsync(
        string cluster,
        string taskDefinitionArn,
        IReadOnlyList<string> subnets,
        IReadOnlyList<string> securityGroups,
        string? region,
        CancellationToken cancellationToken = default)
    {
        var client = GetClient(region);
        var response = await client.RunTaskAsync(
                new RunTaskRequest
                {
                    Cluster = cluster,
                    TaskDefinition = taskDefinitionArn,
                    LaunchType = LaunchType.FARGATE,
                    Count = 1,
                    StartedBy = "honua-release-migrate",
                    NetworkConfiguration = new NetworkConfiguration
                    {
                        AwsvpcConfiguration = new AwsVpcConfiguration
                        {
                            Subnets = [.. subnets],
                            SecurityGroups = [.. securityGroups],
                            AssignPublicIp = AssignPublicIp.DISABLED
                        }
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (response.Failures is { Count: > 0 } || response.Tasks is not { Count: > 0 })
        {
            throw new AmazonECSException("ECS RunTask did not start a migration task.");
        }

        var taskArn = response.Tasks[0].TaskArn;
        if (string.IsNullOrWhiteSpace(taskArn))
        {
            throw new AmazonECSException("ECS RunTask did not return a task ARN.");
        }

        return taskArn;
    }

    public async Task<AwsEcsStoppedTask> DescribeStoppedTaskAsync(
        string cluster,
        string taskArn,
        string containerName,
        string? region,
        CancellationToken cancellationToken = default)
    {
        var client = GetClient(region);
        var response = await client.DescribeTasksAsync(
                new DescribeTasksRequest
                {
                    Cluster = cluster,
                    Tasks = [taskArn]
                },
                cancellationToken)
            .ConfigureAwait(false);

        var task = response.Tasks?.FirstOrDefault();
        if (task is null)
        {
            return new AwsEcsStoppedTask { LastStatus = "MISSING", ExitCode = null };
        }

        var exitCode = MigrationContainerExitCode(task.Containers, containerName);

        return new AwsEcsStoppedTask
        {
            LastStatus = task.LastStatus ?? string.Empty,
            ExitCode = exitCode
        };
    }
    internal static int? MigrationContainerExitCode(List<Container>? containers, string containerName)
    {
        var matches = containers?.Where(container =>
            string.Equals(container.Name, containerName, StringComparison.Ordinal)).ToArray();
        return matches?.Length == 1 ? matches[0].ExitCode : null;
    }

}
