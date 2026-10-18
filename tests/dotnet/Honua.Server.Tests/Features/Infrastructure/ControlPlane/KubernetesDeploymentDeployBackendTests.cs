// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class KubernetesDeploymentDeployBackendTests
{
    private static readonly string Baseline = "registry.example/honua@sha256:" + new string('a', 64);
    private static readonly string Candidate = "registry.example/honua@sha256:" + new string('b', 64);

    [UnitTest]
    public async Task StartUsesNativeDeploymentUidResourceVersionAndImmutableBaselineTests()
    {
        var handler = new DeploymentHandler(Baseline);
        var backend = Backend(handler);
        var result = await backend.StartAsync(Operation());
        result.Status.Should().Be(WorkflowOperationStatus.Submitted);
        result.ObservedRevision.Should().Be(Baseline);
        handler.Paths.Should().OnlyContain(path => path.StartsWith("/apis/apps/v1/namespaces/honua/deployments/api", StringComparison.Ordinal));
        using var patch = JsonDocument.Parse(handler.Patch!);
        patch.RootElement.EnumerateArray().Should().Contain(element => element.GetProperty("op").GetString() == "test" &&
            element.GetProperty("path").GetString() == "/metadata/uid" && element.GetProperty("value").GetString() == "deployment-uid");
        patch.RootElement.EnumerateArray().Should().Contain(element => element.GetProperty("path").GetString() == "/metadata/resourceVersion" &&
            element.GetProperty("value").GetString() == "17");
        patch.RootElement.EnumerateArray().Should().Contain(element => element.GetProperty("op").GetString() == "replace" &&
            element.GetProperty("value").ValueKind == JsonValueKind.String && element.GetProperty("value").GetString() == Candidate);
    }

    [UnitTest]
    public async Task LostPatchResponseRetryRecognizesOnlySameOperationBaselineReceipt()
    {
        var handler = new DeploymentHandler(Candidate, sameCommand: true);
        (await Backend(handler).StartAsync(Operation())).Status.Should().Be(WorkflowOperationStatus.Submitted);
        handler.Patch.Should().BeNull();
        var foreign = new DeploymentHandler(Candidate);
        var invoke = () => Backend(foreign).StartAsync(Operation());
        await invoke.Should().ThrowAsync<InvalidOperationException>();
        foreign.Patch.Should().BeNull();
    }

    [UnitTest]
    public async Task UpgradeLostResponseThenRollbackPermanentlyRefusesLateForwardReplay()
    {
        var handler = new DeploymentHandler(Baseline);
        var backend = Backend(handler);
        await backend.StartAsync(Operation());
        await backend.StartAsync(Operation()); // Same durable operation after an unobserved PATCH response.
        await backend.RollbackAsync(Operation());
        var restoredPatch = handler.Patch;
        var lateStart = () => backend.StartAsync(Operation());
        await lateStart.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback*forward*");
        handler.Patch.Should().Be(restoredPatch);
        var changed = Operation() with { Deploy = Operation().Deploy! with { DesiredRevision = "registry.example/honua@sha256:" + new string('c', 64) } };
        var wrongDesired = () => backend.RollbackAsync(changed);
        await wrongDesired.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task CallerClusterAndRouteOverridesAreRejectedBeforeProviderNetwork()
    {
        var handler = new DeploymentHandler(Baseline);
        var backend = Backend(handler);
        foreach (var key in new[] { "kubernetes.api_server", "kubernetes.cluster_uid", "kubernetes.deployment_uid", "kubernetes.service_uid", "kubernetes.dns_record_name", "kubernetes.dns_zone_id", "kubernetes.load_balancer_arn", "telemetry.healthz.url", "telemetry.golden_query.url", "telemetry.golden_query.expected_contains" })
        {
            var operation = Operation();
            var parameters = new Dictionary<string, string>(operation.Deploy!.Parameters) { [key] = "attacker-controlled" };
            var supplied = operation with { Deploy = operation.Deploy with { Parameters = parameters } };
            (await backend.PlanAsync(supplied.Deploy)).BlockingReasons.Should().NotBeEmpty();
            var invoke = () => backend.StartAsync(supplied);
            await invoke.Should().ThrowAsync<InvalidOperationException>();
        }
        handler.Paths.Should().BeEmpty();
    }

    [UnitTest]
    public async Task RecreatedDeploymentAndChangedRevisionNeverPatch()
    {
        foreach (var handler in new[] { new DeploymentHandler(Baseline, uid: "replacement"), new DeploymentHandler("mutable:latest") })
        {
            var invoke = () => Backend(handler).StartAsync(Operation());
            await invoke.Should().ThrowAsync<InvalidOperationException>();
            handler.Patch.Should().BeNull();
        }
    }

    [UnitTest]
    public async Task RollbackUsesRecordedDigestAndRefusesUnboundForeignDeployment()
    {
        var handler = new DeploymentHandler(Candidate, sameCommand: true);
        (await Backend(handler).RollbackAsync(Operation())).Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        handler.Patch.Should().Contain(Baseline);
        var foreign = new DeploymentHandler(Candidate);
        var invoke = () => Backend(foreign).RollbackAsync(Operation());
        await invoke.Should().ThrowAsync<InvalidOperationException>();
        foreign.Patch.Should().BeNull();
    }

    [UnitTest]
    public async Task SuccessRequiresActualServiceEndpointsRuntimeDigestAndFunctionalHealth()
    {
        var healthy = new DeploymentHandler(Candidate, sameCommand: true);
        (await Backend(healthy).ObserveAsync(Operation())).Status.Should().Be(WorkflowOperationStatus.Succeeded);
        foreach (var provider in new[]
                 {
                     new DeploymentHandler(Candidate, sameCommand: true, missingEndpoints: true),
                     new DeploymentHandler(Candidate, sameCommand: true, wrongPort: true),
                     new DeploymentHandler(Candidate, sameCommand: true, wrongRuntimeDigest: true),
                     new DeploymentHandler(Candidate, sameCommand: true, wrongEndpointPort: true),
                     new DeploymentHandler(Candidate, sameCommand: true, wrongEndpointProtocol: true),
                     new DeploymentHandler(Candidate, sameCommand: true, wrongEndpointAddress: true),
                 })
            (await Backend(provider).ObserveAsync(Operation())).Status.Should().Be(WorkflowOperationStatus.Reconciling);
        (await Backend(new DeploymentHandler(Candidate, sameCommand: true), functionalHealthy: false).ObserveAsync(Operation()))
            .Status.Should().Be(WorkflowOperationStatus.Reconciling);
    }

    [UnitTest]
    public async Task PlanDoesNotAdvertiseCanaryAndRejectsMissingDurableBaseline()
    {
        var backend = Backend(new DeploymentHandler(Baseline));
        var spec = Operation().Deploy!;
        (await backend.PlanAsync(spec)).BlockingReasons.Should().BeEmpty();
        (await backend.GetCapabilitiesAsync()).SupportsTrafficShifting.Should().BeFalse();
        (await backend.PlanAsync(spec with { CurrentRevision = "honua:latest" })).BlockingReasons.Should().NotBeEmpty();
        var parameters = new Dictionary<string, string>(spec.Parameters) { ["deployment.canary_weight_percentage"] = "10" };
        (await backend.PlanAsync(spec with { Parameters = parameters })).BlockingReasons.Should().NotBeEmpty();
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CustomDomainRequiresCloudProofForTheActualObservedKubernetesLoadBalancer(bool proved)
    {
        var operation = Operation();
        var parameters = new Dictionary<string, string>(operation.Deploy!.Parameters)
        {
            ["kubernetes.public_url"] = "https://maps.example.test:8080",
            ["telemetry.healthz.url"] = "https://maps.example.test:8080/healthz/ready",
            ["telemetry.golden_query.url"] = "https://maps.example.test:8080/ogc/features/collections/test/items",
            ["kubernetes.dns_record_name"] = "maps.example.test",
            ["kubernetes.dns_zone_id"] = "ZPUBLIC",
            ["kubernetes.load_balancer_arn"] = "arn:aws:elasticloadbalancing:us-west-2:123456789012:loadbalancer/net/honua/123"
        };
        operation = operation with { Deploy = operation.Deploy with { Parameters = parameters } };
        var verifier = Substitute.For<IKubernetesPublicRouteVerifier>();
        verifier.VerifyAsync(parameters, "native.example", Arg.Any<CancellationToken>()).Returns(proved);
        var handler = new DeploymentHandler(Candidate, sameCommand: true);
        var backend = Backend(handler, registeredSpec: operation.Deploy, routeVerifier: verifier);
        (await backend.ObserveAsync(operation)).Status.Should().Be(proved ? WorkflowOperationStatus.Succeeded : WorkflowOperationStatus.Reconciling);
        await verifier.Received(1).VerifyAsync(parameters, "native.example", Arg.Any<CancellationToken>());
        (await Backend(new DeploymentHandler(Candidate, sameCommand: true), registeredSpec: operation.Deploy).ObserveAsync(operation))
            .Status.Should().Be(WorkflowOperationStatus.Reconciling, "a healthy response is insufficient without exact DNS/LB proof");
    }

    private static KubernetesDeploymentDeployBackend Backend(DeploymentHandler handler, bool functionalHealthy = true, DeployOperationSpec? registeredSpec = null, IKubernetesPublicRouteVerifier? routeVerifier = null)
    {
        var factory = Substitute.For<IBoundKubernetesConnectionFactory>();
        factory.OpenAsync(Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => new BoundKubernetesConnection(new HttpClient(handler, disposeHandler: false), new Uri("https://cluster.example"), null, "host-token"));
        var targets = Substitute.For<IDeployTargetRegistry>();
        var spec = registeredSpec ?? Operation().Deploy!;
        targets.GetAsync(spec.TargetId, Arg.Any<CancellationToken>()).Returns(new DeployTargetDefinition
        {
            TargetId = spec.TargetId,
            TargetKind = spec.TargetKind,
            Backend = spec.Backend,
            Environment = spec.Environment,
            TargetName = spec.TargetName,
            Parameters = spec.Parameters,
        });
        var rollbackProbe = Substitute.For<IRollbackDataPlaneProbe>();
        rollbackProbe.ProbeReadinessAsync(Arg.Any<RollbackReadinessProbeRequest>(), Arg.Any<CancellationToken>()).Returns(new RollbackReadinessProbeResult { Proven = true });
        var forwardProbe = Substitute.For<IDeployHealthProbe>();
        forwardProbe.ProbeGoldenQueryAsync(Arg.Any<DeployGoldenQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DeployGoldenQueryResult { Validated = true, Matched = functionalHealthy });
        return new(factory, targets, rollbackProbe, forwardProbe, routeVerifier);
    }

    private static WorkflowOperationRecord Operation() => new()
    {
        OperationId = "operation-1",
        Kind = WorkflowOperationKind.Deploy,
        Status = WorkflowOperationStatus.Submitted,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
        Deploy = new()
        {
            TargetId = "eks-test",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = KubernetesDeploymentDeployBackend.AdapterBackendName,
            Environment = "isolated",
            TargetName = "api",
            CurrentRevision = Baseline,
            DesiredRevision = Candidate,
            Parameters = new Dictionary<string, string>
            {
                ["kubernetes.cluster_arn"] = "arn:aws:eks:us-west-2:123456789012:cluster/test",
                ["kubernetes.api_server"] = "https://cluster.example",
                ["kubernetes.ca_sha256"] = new string('c', 64),
                ["telemetry.healthz.url"] = "https://native.example:8080/healthz/ready",
                ["telemetry.golden_query.url"] = "https://native.example:8080/ogc/features/collections/test/items",
                ["telemetry.golden_query.expected_contains"] = "retained-dataset",
                ["kubernetes.public_url"] = "https://native.example:8080",
                ["kubernetes.service_port"] = "8080",
                ["kubernetes.target_port"] = "8080",
                ["kubernetes.service_name"] = "api",
                ["kubernetes.service_uid"] = "service-uid",
                ["kubernetes.route_kind"] = "LoadBalancerService",
                ["kubernetes.route_name"] = "api",
                ["kubernetes.route_uid"] = "service-uid",
                ["kubernetes.cluster_uid"] = "cluster-uid",
                ["kubernetes.namespace"] = "honua",
                ["kubernetes.deployment_name"] = "api",
                ["kubernetes.deployment_uid"] = "deployment-uid",
                ["kubernetes.container_name"] = "honua",
            },
        },
    };

    private sealed class DeploymentHandler(string image, bool sameCommand = false, string uid = "deployment-uid", string phase = "forward", bool missingEndpoints = false, bool wrongPort = false, bool wrongRuntimeDigest = false, bool wrongEndpointPort = false, bool wrongEndpointProtocol = false,
        bool wrongEndpointAddress = false) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string? Patch { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization!.Parameter.Should().Be("host-token");
            Paths.Add(request.RequestUri!.AbsolutePath);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/services/api", StringComparison.Ordinal)) return Json($$$$"""
                {"metadata":{"uid":"service-uid"},"spec":{"selector":{"app":"honua"},"type":"LoadBalancer","ports":[{"port":{{{{(wrongPort ? 9999 : 8080)}}}},"targetPort":8080,"protocol":"TCP"}]},
                 "status":{"loadBalancer":{"ingress":[{"hostname":"native.example"}]}}}
                """);
            if (path.EndsWith("/replicasets", StringComparison.Ordinal)) return Json("""
                {"items":[{"metadata":{"uid":"replicaset-uid","ownerReferences":[{"uid":"deployment-uid","controller":true}]}}]}
                """);
            if (path.EndsWith("/pods", StringComparison.Ordinal)) return Json($$$"""
                {"items":[{"metadata":{"uid":"pod-uid","ownerReferences":[{"uid":"replicaset-uid","controller":true}]},
                  "status":{"phase":"Running","podIP":"10.0.0.8","containerStatuses":[{"name":"honua","ready":true,"imageID":"{{{(wrongRuntimeDigest ? Baseline : image)}}}"}]}}]}
                """);
            if (path.EndsWith("/endpointslices", StringComparison.Ordinal)) return Json(missingEndpoints ? "{\"items\":[]}" : $$$"""
                {"items":[{"metadata":{"ownerReferences":[{"uid":"service-uid","controller":true}]},"addressType":"IPv4",
                  "ports":[{"port":{{{(wrongEndpointPort ? 9999 : 8080)}}},"protocol":"{{{(wrongEndpointProtocol ? "UDP" : "TCP")}}}"}],
                  "endpoints":[{"addresses":["{{{(wrongEndpointAddress ? "10.0.0.99" : "10.0.0.8")}}}"],"conditions":{"ready":true},"targetRef":{"kind":"Pod","uid":"pod-uid"}}]}]}
                """);
            if (request.Method == HttpMethod.Patch)
            {
                Patch = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var patch = JsonDocument.Parse(Patch);
                foreach (var element in patch.RootElement.EnumerateArray())
                {
                    if (element.GetProperty("op").GetString() == "replace") image = element.GetProperty("value").GetString()!;
                    if (element.GetProperty("path").GetString() == "/metadata/annotations")
                    {
                        sameCommand = true;
                        phase = element.GetProperty("value").GetProperty("honua.io/deploy-phase").GetString()!;
                    }
                }
            }
            var annotations = sameCommand ? $"\"honua.io/deploy-operation\":\"operation-1\",\"honua.io/deploy-baseline\":\"{Baseline}\",\"honua.io/deploy-desired\":\"{Candidate}\",\"honua.io/deploy-phase\":\"{phase}\"" : "";
            var json = $$$$"""
                {"metadata":{"uid":"{{{{uid}}}}","resourceVersion":"17","generation":3,"annotations":{ {{{{annotations}}}} }},
                 "spec":{"replicas":1,"template":{"spec":{"containers":[{"name":"honua","image":"{{{{image}}}}","ports":[{"containerPort":8080}]}]}},"selector":{"matchLabels":{"app":"honua"}}},
                 "status":{"observedGeneration":3,"replicas":1,"availableReplicas":1,"updatedReplicas":1}}
                """;
            return Json(json);
        }
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
