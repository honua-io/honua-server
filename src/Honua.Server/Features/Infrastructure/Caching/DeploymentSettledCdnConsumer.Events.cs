// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.Caching;

internal sealed partial class DeploymentSettledCdnConsumer
{
    private static Deployment? Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (Text(root, "source") != "aws.ecs" || Text(root, "detail-type") != "ECS Deployment State Change" ||
            !root.TryGetProperty("detail", out var detail) || Text(detail, "eventName") != "SERVICE_DEPLOYMENT_COMPLETED")
        {
            return null;
        }

        if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array ||
            resources.GetArrayLength() != 1 || resources[0].ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var service = resources[0].GetString()!;
        var arn = service.Split(':', 6);
        var address = arn.Length == 6 ? arn[5].Split('/') : [];
        var deploymentId = Text(detail, "deploymentId");
        if (arn.Length != 6 || arn[0] != "arn" || arn[2] != "ecs" || address.Length != 3 ||
            address[0] != "service" || address[1].Length == 0 || address[2].Length == 0 ||
            arn[3] != Text(root, "region") || deploymentId.Length == 0)
        {
            return null;
        }

        var cluster = string.Join(':', arn.Take(5)) + ":cluster/" + address[1];
        return new Deployment(cluster, address[1], service, address[2], arn[3], deploymentId);
    }

    private static bool IsConfiguredTarget(Deployment deployment, ControlPlaneOptions configuration)
        => configuration.DeployTargets.Any(target =>
            target.TargetKind == DeployTargetKind.AwsEcs &&
            target.Backend is "honua-aws-ecs-alb" or "honua-gitops-aws-ecs" &&
            Parameter(target, "aws.region") == deployment.Region &&
            Parameter(target, "aws.ecs.cluster") is { } cluster &&
            (cluster == deployment.ClusterArn || cluster == deployment.ClusterName) &&
            MatchesService(target, deployment));

    private static bool MatchesService(DeployTargetOptions target, Deployment deployment)
    {
        var canary = Parameter(target, "aws.ecs.canary_service");
        if (canary is null && (target.Parameters.ContainsKey("aws.ecs.canary_service") ||
            target.ParameterEntries.Any(entry => entry.Key == "aws.ecs.canary_service")))
        {
            return false;
        }

        canary ??= target.TargetName;
        var stable = Parameter(target, "aws.ecs.stable_service");
        return canary == deployment.ServiceName || canary == deployment.ServiceArn ||
            stable == deployment.ServiceName || stable == deployment.ServiceArn;
    }

    private static string? Parameter(DeployTargetOptions target, string key)
    {
        var values = target.ParameterEntries.Where(entry => entry.Key == key).Select(entry => entry.Value).ToList();
        if (target.Parameters.TryGetValue(key, out var value))
        {
            values.Add(value);
        }

        return values.Distinct(StringComparer.Ordinal).ToArray() is [var single] && !string.IsNullOrWhiteSpace(single)
            ? single : null;
    }

    private static string Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private sealed record Deployment(string ClusterArn, string ClusterName, string ServiceArn,
        string ServiceName, string Region, string DeploymentId);
}
#endif
