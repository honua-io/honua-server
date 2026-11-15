// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using System.Text.Json;
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
        return new Deployment
        {
            ClusterArn = cluster,
            ClusterName = address[1],
            ServiceArn = service,
            ServiceName = address[2],
            Region = arn[3],
            DeploymentId = deploymentId,
        };
    }

    // Targets come from the registry the deploy backends use, and parameters are read the way the ECS/ALB
    // backend reads them: trimmed, blank as missing, the canary defaulting to the target name, the region
    // optional (the backend then uses the SDK default, so any event region matches).
    private static bool IsConfiguredTarget(Deployment deployment, IReadOnlyList<DeployTargetDefinition> targets)
        => targets.Any(target =>
            target.TargetKind == DeployTargetKind.AwsEcs &&
            target.Backend is "honua-aws-ecs-alb" or "honua-gitops-aws-ecs" &&
            (Parameter(target, "aws.region") ?? deployment.Region) == deployment.Region &&
            Parameter(target, "aws.ecs.cluster") is { } cluster &&
            (cluster == deployment.ClusterArn || cluster == deployment.ClusterName) &&
            MatchesService(target, deployment));

    private static bool MatchesService(DeployTargetDefinition target, Deployment deployment)
    {
        var canary = Parameter(target, "aws.ecs.canary_service") ?? target.TargetName.Trim();
        var stable = Parameter(target, "aws.ecs.stable_service");
        return canary == deployment.ServiceName || canary == deployment.ServiceArn ||
            stable == deployment.ServiceName || stable == deployment.ServiceArn;
    }

    private static string? Parameter(DeployTargetDefinition target, string key)
        => target.Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private sealed record Deployment
    {
        public required string ClusterArn { get; init; }

        public required string ClusterName { get; init; }

        public required string ServiceArn { get; init; }

        public required string ServiceName { get; init; }

        public required string Region { get; init; }

        public required string DeploymentId { get; init; }
    }
}
#endif
