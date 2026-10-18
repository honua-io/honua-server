// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Collection("Database")]
public sealed class KubernetesDeploymentAuthorityHttpTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/plan")]
    public async Task ActualPlanRequestCannotReplaceRegisteredClusterIdentityBeforeProviderNetwork()
    {
        var targets = Substitute.For<IDeployTargetRegistry>();
        var connections = Substitute.For<IBoundKubernetesConnectionFactory>();
        var parameters = new Dictionary<string, string>
        {
            ["honua.runtime_kind"] = "aws-eks", ["kubernetes.cluster_arn"] = "arn:aws:eks:us-west-2:123456789012:cluster/test",
            ["kubernetes.api_server"] = "https://registered.example", ["kubernetes.ca_sha256"] = new string('a', 64),
            ["kubernetes.cluster_uid"] = "registered-cluster", ["kubernetes.namespace"] = "honua", ["kubernetes.deployment_name"] = "api",
            ["kubernetes.deployment_uid"] = "registered-deployment", ["kubernetes.container_name"] = "honua",
            ["kubernetes.service_name"] = "api", ["kubernetes.service_uid"] = "registered-service",
            ["telemetry.healthz.url"] = "https://native.example:8080/healthz/ready",
                ["telemetry.golden_query.url"] = "https://native.example:8080/ogc/features/collections/test/items",
                ["telemetry.golden_query.expected_contains"] = "retained-dataset",
                ["kubernetes.public_url"] = "https://native.example:8080", ["kubernetes.service_port"] = "8080", ["kubernetes.target_port"] = "8080",
            ["kubernetes.route_kind"] = "LoadBalancerService", ["kubernetes.route_name"] = "api", ["kubernetes.route_uid"] = "registered-service",
        };
        var target = new DeployTargetDefinition
        {
            TargetId = "native-eks", TargetKind = DeployTargetKind.Kubernetes, Backend = KubernetesDeploymentDeployBackend.AdapterBackendName,
            Environment = "isolated", TargetName = "api", Parameters = parameters,
        };
        targets.GetAsync("native-eks", Arg.Any<CancellationToken>()).Returns(target);
        targets.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { target });
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IDeployTargetRegistry>(); services.AddSingleton(targets);
            services.RemoveAll<IBoundKubernetesConnectionFactory>(); services.AddSingleton(connections);
        });
        try
        {
            await fixture.InitializeAsync();
            fixture.Services.GetService<IArgoRolloutsClient>().Should().BeNull();
            fixture.Services.GetService<KubernetesArgoRolloutsDeployBackend>().Should().BeNull();
            fixture.Services.GetServices<IDeployBackend>().Should().NotContain(backend => backend.BackendName.Contains("argo", StringComparison.OrdinalIgnoreCase));
            using var client = fixture.CreateAdminClient();
            foreach (var key in new[] { "kubernetes.api_server", "kubernetes.ca_sha256", "kubernetes.cluster_uid", "kubernetes.deployment_uid", "kubernetes.dns_record_name", "kubernetes.dns_zone_id", "kubernetes.load_balancer_arn", "telemetry.healthz.url", "telemetry.golden_query.url", "telemetry.golden_query.expected_contains" })
            {
                using var response = await client.PostAsJsonAsync("/api/v1/admin/deploy/plan", new
                {
                    targetId = target.TargetId, currentRevision = "registry.example/honua@sha256:" + new string('b', 64),
                    desiredRevision = "registry.example/honua@sha256:" + new string('c', 64), parameters = new Dictionary<string, string> { [key] = "caller-override" },
                });
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                result.RootElement.GetProperty("backendRegistered").GetBoolean().Should().BeTrue();
                result.RootElement.GetProperty("readyToSubmit").GetBoolean().Should().BeFalse();
                result.RootElement.GetProperty("blockingReasons").EnumerateArray().Should().Contain(reason =>
                    reason.GetString()!.Contains("current registered native Deployment target", StringComparison.Ordinal));
            }
            await connections.DidNotReceiveWithAnyArgs().OpenAsync(default!, default);
        }
        finally { await fixture.DisposeAsync(); }
    }
}
