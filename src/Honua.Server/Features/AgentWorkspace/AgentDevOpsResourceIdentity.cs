// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.AgentWorkspace;

internal static class AgentDevOpsResourceIdentity
{
    // These are registry facts, not observations under the child process's cloud
    // credentials. Preserve missing pins and alias precedence for the consumer to
    // validate; never serialize all target parameters (which may hold secrets).
    internal static Dictionary<string, string> Project(DeployTargetDefinition target, string runtimeKind)
    {
        string[] keys = runtimeKind switch
        {
            "aws-lambda" =>
            [
                "aws.region", "lambda.region", "target.resource_id",
                "aws.lambda.function_name", "lambda.function_name", "lambda.alias_function_name",
                "aws.lambda.alias_name", "lambda.alias_name", "lambda.alias",
                "lambda.function_arn", "lambda.alias_arn",
            ],
            "aws-ecs" =>
            [
                "aws.region", "aws.ecs.cluster", "aws.ecs.canary_service", "aws.ecs.stable_service",
                "aws.alb.listener_rule_arn", "aws.alb.canary_target_group_arn", "aws.alb.stable_target_group_arn",
            ],
            "aws-eks" =>
            [
                "kubernetes.cluster_arn", "kubernetes.ca_sha256", "kubernetes.cluster_uid",
                "kubernetes.namespace", "kubernetes.deployment_name", "kubernetes.deployment_uid", "kubernetes.container_name",
                "kubernetes.service_name", "kubernetes.service_uid", "kubernetes.service_port", "kubernetes.target_port",
                "kubernetes.route_kind", "kubernetes.route_name", "kubernetes.route_uid",
                "kubernetes.route_host", "kubernetes.route_path", "kubernetes.route_path_type",
                "kubernetes.load_balancer_arn", "kubernetes.dns_zone_id", "kubernetes.dns_record_name",
            ],
            _ => [],
        };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (target.Parameters.TryGetValue(key, out var value)) parameters.Add(key, value);
        }
        return parameters;
    }
}
