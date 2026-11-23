// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Amazon.ECS;
using Amazon.ECS.Model;

namespace Honua.Aws.Features.ControlPlane;

internal static class QualificationEcsDeployment
{
    private static readonly HttpClient _metadata = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });

    public static async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        var endpoint = Environment.GetEnvironmentVariable("ECS_CONTAINER_METADATA_URI_V4");
        if (!Uri.TryCreate(endpoint + "/task", UriKind.Absolute, out var taskEndpoint)
            || taskEndpoint.Scheme != Uri.UriSchemeHttp
            || taskEndpoint.Host != "169.254.170.2"
            || !taskEndpoint.IsDefaultPort
            || taskEndpoint.UserInfo.Length != 0)
        {
            throw new InvalidOperationException("The ECS task metadata endpoint is unavailable.");
        }

        using var stream = await _metadata.GetStreamAsync(taskEndpoint, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var cluster = document.RootElement.GetProperty("Cluster").GetString();
        var task = document.RootElement.GetProperty("TaskARN").GetString();
        if (string.IsNullOrWhiteSpace(cluster) || string.IsNullOrWhiteSpace(task))
        {
            throw new InvalidOperationException("ECS task metadata has no cluster or task ARN.");
        }

        using var client = new AmazonECSClient();
        var response = await client.DescribeTasksAsync(new DescribeTasksRequest { Cluster = cluster, Tasks = [task] },
            cancellationToken).ConfigureAwait(false);
        var deployment = response.Tasks?.SingleOrDefault()?.StartedBy;
        return !string.IsNullOrWhiteSpace(deployment)
            ? deployment
            : throw new InvalidOperationException("ECS did not identify the task's deployment.");
    }
}
