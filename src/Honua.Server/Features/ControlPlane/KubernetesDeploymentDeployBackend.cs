// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>Native Deployment rolling updates through the canonical Honua deploy reconciler.</summary>
internal sealed class KubernetesDeploymentDeployBackend(IBoundKubernetesConnectionFactory connections, IDeployTargetRegistry targets,
    IRollbackDataPlaneProbe? dataPlaneProbe = null, IDeployHealthProbe? forwardHealthProbe = null,
    IKubernetesPublicRouteVerifier? publicRouteVerifier = null) : IDeployBackend
{
    internal const string AdapterBackendName = "honua-kubernetes-deployment";
    private const string OperationAnnotation = "honua.io/deploy-operation";
    private const string BaselineAnnotation = "honua.io/deploy-baseline";
    private const string DesiredAnnotation = "honua.io/deploy-desired";
    private const string PhaseAnnotation = "honua.io/deploy-phase";
    public string BackendName => AdapterBackendName;
    public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = true, SupportsProgressPolling = true, SupportsRevisionPinning = true,
            RequiresOutOfBandMigrations = true, SupportsTrafficShifting = false, SupportsCancellation = false,
        });

    public async Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> reasons = [];
        try { await RequireRegisteredTargetAsync(spec, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { reasons.Add("The operation does not match the current registered native Deployment target."); }
        if (spec.TargetKind != TargetKind || spec.Backend != BackendName) reasons.Add("The native Deployment backend requires its exact registered target kind and backend.");
        foreach (var key in new[] { "kubernetes.cluster_arn", "kubernetes.api_server", "kubernetes.ca_sha256", "kubernetes.cluster_uid",
                     "kubernetes.namespace", "kubernetes.deployment_name", "kubernetes.deployment_uid", "kubernetes.container_name",
                     "kubernetes.service_name", "kubernetes.service_uid", "kubernetes.service_port", "kubernetes.target_port",
                     "kubernetes.route_kind", "kubernetes.route_name", "kubernetes.route_uid", "kubernetes.public_url" })
            if (!spec.Parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) reasons.Add($"A registered {key} is required.");
        if (spec.Parameters.GetValueOrDefault("kubernetes.route_kind") == "Ingress")
            foreach (var key in new[] { "kubernetes.route_host", "kubernetes.route_path", "kubernetes.route_path_type" })
                if (string.IsNullOrWhiteSpace(spec.Parameters.GetValueOrDefault(key))) reasons.Add($"An exact registered {key} is required.");
        var proof = RollbackDataPlaneCompletion.ReadFunctionalExpectation(spec.Parameters);
        if (!proof.IsConfigured || string.IsNullOrWhiteSpace(proof.Url) ||
            !spec.Parameters.TryGetValue(RollbackDataPlaneCompletion.ReadinessUrlParameterKey, out var readinessUrl) || string.IsNullOrWhiteSpace(readinessUrl))
            reasons.Add("Registered readiness and functional-query expectations are required before changing the Deployment.");
        if (!IsImmutableImage(spec.CurrentRevision) || !IsImmutableImage(spec.DesiredRevision))
            reasons.Add("Both durable baseline and desired revision must be full immutable image@sha256 references.");
        if (spec.Parameters.ContainsKey("kubernetes.argo.canary_weight_percentage") || spec.Parameters.ContainsKey("deployment.canary_weight_percentage"))
            reasons.Add("Native Deployment rolling updates do not implement weighted traffic canaries.");
        return new DeployPlan
        {
            IsReadyToSubmit = reasons.Count == 0 && !spec.RequiresApproval, RequiresApproval = spec.RequiresApproval,
            RequiresOutOfBandMigrations = true, BlockingReasons = reasons,
        };
    }

    public async Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
    {
        var spec = RequiredSpec(operation);
        await MutateAsync(operation, rollback: false, cancellationToken).ConfigureAwait(false);
        return new() { Status = WorkflowOperationStatus.Submitted, ProviderOperationId = ProviderId(spec), ObservedRevision = spec.CurrentRevision };
    }

    public Task<DeployObservation> PromoteAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        => ObserveAsync(operation, cancellationToken);

    public async Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
    {
        await MutateAsync(operation, rollback: true, cancellationToken).ConfigureAwait(false);
        return new() { Status = WorkflowOperationStatus.RollbackRequested, ProviderOperationId = ProviderId(RequiredSpec(operation)),
            Message = "The recorded immutable baseline was restored to the Deployment template; convergence remains pending." };
    }

    public async Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
    {
        var spec = RequiredSpec(operation);
        await RequireRegisteredTargetAsync(spec, cancellationToken).ConfigureAwait(false);
        using var connection = await connections.OpenAsync(spec.Parameters, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDeploymentAsync(connection, spec, cancellationToken).ConfigureAwait(false);
        var deployment = document.RootElement;
        var snapshot = ReadSnapshot(deployment, spec);
        if (!HasOperationReceipt(deployment, operation.OperationId, spec.CurrentRevision!, spec.DesiredRevision,
                operation.Status == WorkflowOperationStatus.RollbackRequested ? "rollback" : "forward"))
            throw new InvalidOperationException("The Deployment no longer carries the reviewed operation receipt.");
        var rollback = operation.Status == WorkflowOperationStatus.RollbackRequested;
        var expected = rollback ? spec.CurrentRevision! : spec.DesiredRevision;
        var status = deployment.TryGetProperty("status", out var actualStatus) ? actualStatus : default;
        var replicas = deployment.GetProperty("spec").TryGetProperty("replicas", out var count) ? count.GetInt32() : 1;
        // Scale-to-zero cannot establish serving health or a restored runtime.
        var converged = replicas > 0 && snapshot.Image == expected && Number(status, "observedGeneration") >= snapshot.Generation &&
            Number(status, "updatedReplicas") == replicas && Number(status, "availableReplicas") == replicas &&
            Number(status, "replicas") == replicas && Number(status, "unavailableReplicas") == 0;
        if (converged) converged = await VerifyServingPodsAsync(connection, spec, deployment, expected, replicas, cancellationToken).ConfigureAwait(false);
        if (converged && !rollback)
        {
            var expectation = RollbackDataPlaneCompletion.ReadFunctionalExpectation(spec.Parameters);
            var ready = await RollbackDataPlaneCompletion.ProbeReadinessAsync(dataPlaneProbe, spec.Parameters, cancellationToken).ConfigureAwait(false);
            if (forwardHealthProbe is null || ready != true || !expectation.IsConfigured || string.IsNullOrWhiteSpace(expectation.Url))
                converged = false;
            else
            {
                var query = await forwardHealthProbe.ProbeGoldenQueryAsync(new DeployGoldenQueryRequest
                {
                    Url = expectation.Url, ExpectedBodyContains = expectation.ExpectedContains, ExpectedSha256 = expectation.ExpectedSha256,
                    ForbiddenBodyContains = expectation.ForbiddenContains, ExpectedStatusCode = expectation.ExpectedStatusCode,
                    TimeoutSeconds = expectation.TimeoutSeconds,
                }, cancellationToken).ConfigureAwait(false);
                converged = query.Validated && query.Matched;
            }
        }
        if (converged && rollback)
        {
            var readiness = await RollbackDataPlaneCompletion.ProbeReadinessAsync(dataPlaneProbe, spec.Parameters, cancellationToken).ConfigureAwait(false);
            var functional = await RollbackDataPlaneCompletion.ProbeFunctionalQueryAsync(dataPlaneProbe, spec.Parameters, cancellationToken).ConfigureAwait(false);
            var decision = RollbackDataPlaneCompletion.Evaluate(new RollbackDataPlaneEvidence
            {
                RoutingConverged = true, PriorRevisionIdentityProven = true, ServingRevision = snapshot.Image,
                HealthyEndpointCount = readiness == true ? replicas : 0, RegisteredEndpointCount = replicas, FunctionalQuery = functional,
                RollbackStartedAt = RollbackDataPlaneCompletion.ReadObservationStartedAt(spec.Parameters),
                Window = RollbackDataPlaneCompletion.ReadObservationWindow(spec.Parameters),
            }, DateTimeOffset.UtcNow);
            return RollbackDataPlaneCompletion.ToDeployObservation(decision, ProviderId(spec));
        }
        var failed = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("conditions", out var conditions) &&
            conditions.EnumerateArray().Any(condition => condition.GetProperty("type").GetString() == "Progressing" &&
                condition.GetProperty("status").GetString() == "False" && condition.TryGetProperty("reason", out var reason) &&
                reason.GetString() == "ProgressDeadlineExceeded");
        return new()
        {
            Status = converged ? WorkflowOperationStatus.Succeeded : (rollback ? WorkflowOperationStatus.RollbackRequested : WorkflowOperationStatus.Reconciling),
            ProviderOperationId = ProviderId(spec), ObservedRevision = snapshot.Image,
            RollbackRecommended = !rollback && failed,
            Message = converged ? "Native Deployment replicas and immutable runtime image converged." : "Native Deployment convergence remains pending.",
        };
    }

    private async Task MutateAsync(WorkflowOperationRecord operation, bool rollback, CancellationToken cancellationToken)
    {
        var spec = RequiredSpec(operation);
        var plan = await PlanAsync(spec, cancellationToken).ConfigureAwait(false);
        if (plan.BlockingReasons.Count != 0) throw new InvalidOperationException("The registered native Deployment target is incomplete.");
        await RequireRegisteredTargetAsync(spec, cancellationToken).ConfigureAwait(false);
        using var connection = await connections.OpenAsync(spec.Parameters, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDeploymentAsync(connection, spec, cancellationToken).ConfigureAwait(false);
        var deployment = document.RootElement;
        var snapshot = ReadSnapshot(deployment, spec);
        var desired = rollback ? spec.CurrentRevision! : spec.DesiredRevision;
        var prior = rollback ? spec.DesiredRevision : spec.CurrentRevision!;
        var annotations = deployment.GetProperty("metadata").TryGetProperty("annotations", out var existing)
            ? existing.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var sameCommand = annotations.GetValueOrDefault(OperationAnnotation) == operation.OperationId &&
            annotations.GetValueOrDefault(BaselineAnnotation) == spec.CurrentRevision &&
            annotations.GetValueOrDefault(DesiredAnnotation) == spec.DesiredRevision;
        var phase = annotations.GetValueOrDefault(PhaseAnnotation);
        if (!rollback && annotations.GetValueOrDefault(OperationAnnotation) == operation.OperationId && phase == "rollback")
            throw new InvalidOperationException("The operation has entered rollback; forward replay is forbidden.");
        if (annotations.GetValueOrDefault(OperationAnnotation) == operation.OperationId && !sameCommand)
            throw new InvalidOperationException("The operation receipt is bound to a different immutable revision.");
        if (snapshot.Image == desired && sameCommand && phase == (rollback ? "rollback" : "forward")) return; // Lost HTTP response: the exact recorded command already applied.
        if (snapshot.Image != prior) throw new InvalidOperationException("The Deployment revision changed outside the reviewed operation.");
        if (rollback && !sameCommand) throw new InvalidOperationException("The Deployment does not carry this operation's immutable baseline receipt.");
        annotations[OperationAnnotation] = operation.OperationId;
        annotations[BaselineAnnotation] = spec.CurrentRevision!;
        annotations[DesiredAnnotation] = spec.DesiredRevision;
        annotations[PhaseAnnotation] = rollback ? "rollback" : "forward";
        var patch = SerializePatch(snapshot, Required(spec, "kubernetes.container_name"), prior, desired, annotations);
        using var response = await connection.SendAsync(HttpMethod.Patch, DeploymentPath(spec), patch, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] SerializePatch(DeploymentSnapshot snapshot, string containerName, string prior, string desired,
        Dictionary<string, string> annotations)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            Write("test", "/metadata/uid", snapshot.Uid);
            Write("test", "/metadata/resourceVersion", snapshot.ResourceVersion);
            Write("test", $"/spec/template/spec/containers/{snapshot.ContainerIndex}/name", containerName);
            Write("test", $"/spec/template/spec/containers/{snapshot.ContainerIndex}/image", prior);
            Write("replace", $"/spec/template/spec/containers/{snapshot.ContainerIndex}/image", desired);
            writer.WriteStartObject(); writer.WriteString("op", "add"); writer.WriteString("path", "/metadata/annotations");
            writer.WriteStartObject("value");
            foreach (var (key, value) in annotations) writer.WriteString(key, value);
            writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray();
            void Write(string operation, string path, string value)
            {
                writer.WriteStartObject(); writer.WriteString("op", operation); writer.WriteString("path", path);
                writer.WriteString("value", value); writer.WriteEndObject();
            }
        }
        return buffer.ToArray();
    }

    private async Task<bool> VerifyServingPodsAsync(BoundKubernetesConnection connection, DeployOperationSpec spec,
        JsonElement deployment, string expected, int replicas, CancellationToken cancellationToken)
    {
        var selector = deployment.GetProperty("spec").GetProperty("selector");
        if (selector.TryGetProperty("matchExpressions", out var expressions) && expressions.GetArrayLength() > 0) return false;
        if (!selector.TryGetProperty("matchLabels", out var labels) || !labels.EnumerateObject().Any()) return false;
        var query = Uri.EscapeDataString(string.Join(',', labels.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + p.Value.GetString())));
        var ns = Uri.EscapeDataString(Required(spec, "kubernetes.namespace"));
        using var serviceResponse = await connection.SendAsync(HttpMethod.Get,
            $"/api/v1/namespaces/{ns}/services/{Uri.EscapeDataString(Required(spec, "kubernetes.service_name"))}", null, cancellationToken).ConfigureAwait(false);
        using var service = JsonDocument.Parse(await serviceResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (service.RootElement.GetProperty("metadata").GetProperty("uid").GetString() != Required(spec, "kubernetes.service_uid")) return false;
        var serviceSelector = service.RootElement.GetProperty("spec").GetProperty("selector");
        if (!serviceSelector.EnumerateObject().Any()) return false;
        var serviceQuery = Uri.EscapeDataString(string.Join(',', serviceSelector.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + p.Value.GetString())));
        var servicePorts = service.RootElement.GetProperty("spec").GetProperty("ports").EnumerateArray().Where(port =>
            PortMatches(port.GetProperty("port"), Required(spec, "kubernetes.service_port")) ||
            port.TryGetProperty("name", out var portName) && PortMatches(portName, Required(spec, "kubernetes.service_port"))).ToArray();
        if (servicePorts.Length != 1 || !servicePorts[0].TryGetProperty("targetPort", out var targetPort) ||
            !PortMatches(targetPort, Required(spec, "kubernetes.target_port")) ||
            servicePorts[0].TryGetProperty("protocol", out var protocol) && protocol.GetString() != "TCP") return false;
        var container = deployment.GetProperty("spec").GetProperty("template").GetProperty("spec").GetProperty("containers").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == Required(spec, "kubernetes.container_name"));
        if (!container.TryGetProperty("ports", out var declaredPorts)) return false;
        var targetPorts = declaredPorts.EnumerateArray().Where(port =>
            targetPort.ValueKind == JsonValueKind.Number ? port.GetProperty("containerPort").GetInt32() == targetPort.GetInt32() :
            port.TryGetProperty("name", out var name) && name.GetString() == targetPort.GetString()).ToArray();
        if (targetPorts.Length != 1 || targetPorts[0].TryGetProperty("protocol", out var targetProtocol) && targetProtocol.GetString() != "TCP") return false;
        var resolvedTargetPort = targetPorts[0].GetProperty("containerPort").GetInt32();
        var servicePortName = servicePorts[0].TryGetProperty("name", out var selectedName) ? selectedName.GetString() : null;
        if (!await VerifyRouteAsync(connection, spec, service.RootElement, servicePorts[0].GetProperty("port").GetInt32(), cancellationToken).ConfigureAwait(false)) return false;
        using var setsResponse = await connection.SendAsync(HttpMethod.Get, $"/apis/apps/v1/namespaces/{ns}/replicasets?labelSelector={query}", null, cancellationToken).ConfigureAwait(false);
        using var sets = JsonDocument.Parse(await setsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var deploymentUid = Required(spec, "kubernetes.deployment_uid");
        var owners = sets.RootElement.GetProperty("items").EnumerateArray().Where(set => OwnedBy(set, deploymentUid))
            .Select(set => set.GetProperty("metadata").GetProperty("uid").GetString()).ToHashSet(StringComparer.Ordinal);
        if (owners.Count == 0) return false;
        using var podsResponse = await connection.SendAsync(HttpMethod.Get, $"/api/v1/namespaces/{ns}/pods?labelSelector={serviceQuery}", null, cancellationToken).ConfigureAwait(false);
        using var pods = JsonDocument.Parse(await podsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var active = pods.RootElement.GetProperty("items").EnumerateArray().Where(pod =>
            !pod.GetProperty("metadata").TryGetProperty("deletionTimestamp", out var deleted) || deleted.ValueKind == JsonValueKind.Null).ToArray();
        var digest = expected[(expected.LastIndexOf('@') + 1)..];
        var podsValid = active.Length == replicas && active.All(pod => pod.GetProperty("status").TryGetProperty("phase", out var phase) && phase.GetString() == "Running" && owners.Any(owner => owner is not null && OwnedBy(pod, owner)) &&
            pod.GetProperty("status").TryGetProperty("containerStatuses", out var statuses) && statuses.EnumerateArray().Any(container =>
                container.GetProperty("name").GetString() == Required(spec, "kubernetes.container_name") && container.GetProperty("ready").GetBoolean() &&
                container.TryGetProperty("imageID", out var imageId) && imageId.GetString() is { } image &&
                (image == expected || image.EndsWith("@" + digest, StringComparison.Ordinal) || image == "docker-pullable://" + expected || image == "containerd://" + digest || image == "docker://" + digest || image == digest)));
        if (!podsValid) return false;
        var podAddresses = new Dictionary<string, HashSet<System.Net.IPAddress>>(StringComparer.Ordinal);
        foreach (var pod in active)
        {
            var addresses = new HashSet<System.Net.IPAddress>();
            var podStatus = pod.GetProperty("status");
            if (podStatus.TryGetProperty("podIPs", out var ips))
                foreach (var ip in ips.EnumerateArray())
                    if (System.Net.IPAddress.TryParse(ip.GetProperty("ip").GetString(), out var parsed)) addresses.Add(parsed);
            if (podStatus.TryGetProperty("podIP", out var primary) && System.Net.IPAddress.TryParse(primary.GetString(), out var primaryIp)) addresses.Add(primaryIp);
            if (addresses.Count == 0) return false;
            podAddresses.Add(pod.GetProperty("metadata").GetProperty("uid").GetString()!, addresses);
        }
        var endpointSelector = Uri.EscapeDataString("kubernetes.io/service-name=" + Required(spec, "kubernetes.service_name"));
        using var endpointsResponse = await connection.SendAsync(HttpMethod.Get,
            $"/apis/discovery.k8s.io/v1/namespaces/{ns}/endpointslices?labelSelector={endpointSelector}", null, cancellationToken).ConfigureAwait(false);
        using var slices = JsonDocument.Parse(await endpointsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var serving = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slice in slices.RootElement.GetProperty("items").EnumerateArray())
        {
            if (!OwnedBy(slice, Required(spec, "kubernetes.service_uid")) || !slice.TryGetProperty("ports", out var ports) ||
                !slice.TryGetProperty("addressType", out var type) || type.GetString() is not ("IPv4" or "IPv6")) return false;
            var addressFamily = type.GetString() == "IPv4" ? System.Net.Sockets.AddressFamily.InterNetwork : System.Net.Sockets.AddressFamily.InterNetworkV6;
            var selectedPorts = ports.EnumerateArray().Where(port =>
                (port.TryGetProperty("name", out var name) ? name.ValueKind == JsonValueKind.Null ? null : name.GetString() : null) == servicePortName).ToArray();
            if (selectedPorts.Length != 1 || !selectedPorts[0].TryGetProperty("port", out var actualPort) ||
                actualPort.ValueKind != JsonValueKind.Number || actualPort.GetInt32() != resolvedTargetPort ||
                !selectedPorts[0].TryGetProperty("protocol", out var actualProtocol) || actualProtocol.GetString() != "TCP") return false;
            foreach (var endpoint in slice.GetProperty("endpoints").EnumerateArray())
            {
                if (!endpoint.TryGetProperty("conditions", out var health) || !health.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True) continue;
                if (!endpoint.TryGetProperty("targetRef", out var target) || target.GetProperty("kind").GetString() != "Pod" ||
                    target.GetProperty("uid").GetString() is not { } uid || !podAddresses.TryGetValue(uid, out var expectedAddresses) ||
                    !endpoint.TryGetProperty("addresses", out var addresses) || addresses.GetArrayLength() == 0 ||
                    addresses.EnumerateArray().Any(address => !System.Net.IPAddress.TryParse(address.GetString(), out var parsed) || parsed.AddressFamily != addressFamily || !expectedAddresses.Contains(parsed))) return false;
                serving.Add(uid);
            }
        }
        return serving.Count == replicas;
    }

    private static bool HasOperationReceipt(JsonElement deployment, string operationId, string baseline, string desired, string phase)
        => deployment.GetProperty("metadata").TryGetProperty("annotations", out var annotations) &&
            annotations.TryGetProperty(OperationAnnotation, out var operation) && operation.GetString() == operationId &&
            annotations.TryGetProperty(BaselineAnnotation, out var original) && original.GetString() == baseline &&
            annotations.TryGetProperty(DesiredAnnotation, out var candidate) && candidate.GetString() == desired &&
            annotations.TryGetProperty(PhaseAnnotation, out var direction) && direction.GetString() == phase;

    private async Task<bool> VerifyRouteAsync(BoundKubernetesConnection connection, DeployOperationSpec spec,
        JsonElement service, int servicePort, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(Required(spec, "kubernetes.public_url"), UriKind.Absolute, out var publicUrl) || publicUrl.Scheme != "https") return false;
        List<Uri> proofUrls = [];
        foreach (var key in new[] { RollbackDataPlaneCompletion.ReadinessUrlParameterKey, RollbackDataPlaneCompletion.FunctionalQueryUrlParameterKey })
        {
            if (!spec.Parameters.TryGetValue(key, out var raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var probeUrl) ||
                probeUrl.Scheme != publicUrl.Scheme || probeUrl.IdnHost != publicUrl.IdnHost || probeUrl.Port != publicUrl.Port ||
                !probeUrl.AbsolutePath.StartsWith(publicUrl.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal)) return false;
            proofUrls.Add(probeUrl);
        }
        var kind = Required(spec, "kubernetes.route_kind");
        if (kind == "LoadBalancerService")
        {
            if (Required(spec, "kubernetes.route_name") != Required(spec, "kubernetes.service_name") ||
                Required(spec, "kubernetes.route_uid") != Required(spec, "kubernetes.service_uid") ||
                service.GetProperty("spec").GetProperty("type").GetString() != "LoadBalancer" || publicUrl.Port != servicePort) return false;
            return await VerifyPublicBindingAsync(service, spec, publicUrl, requireCloudProof: false, cancellationToken).ConfigureAwait(false);
        }
        if (kind != "Ingress" || Required(spec, "kubernetes.route_host") != publicUrl.IdnHost || publicUrl.Port != 443 ||
            !publicUrl.AbsolutePath.StartsWith(Required(spec, "kubernetes.route_path").TrimEnd('/') + "/", StringComparison.Ordinal) &&
            publicUrl.AbsolutePath.TrimEnd('/') != Required(spec, "kubernetes.route_path").TrimEnd('/')) return false;
        var routePath = Required(spec, "kubernetes.route_path");
        var pathType = Required(spec, "kubernetes.route_path_type");
        if (pathType is not ("Exact" or "Prefix") || proofUrls.Any(probe => pathType == "Exact"
                ? probe.AbsolutePath != routePath
                : probe.AbsolutePath != routePath && !probe.AbsolutePath.StartsWith(routePath.TrimEnd('/') + "/", StringComparison.Ordinal))) return false;
        var ns = Uri.EscapeDataString(Required(spec, "kubernetes.namespace"));
        var ingressName = Uri.EscapeDataString(Required(spec, "kubernetes.route_name"));
        using var response = await connection.SendAsync(HttpMethod.Get, $"/apis/networking.k8s.io/v1/namespaces/{ns}/ingresses/{ingressName}", null, cancellationToken).ConfigureAwait(false);
        using var ingress = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (ingress.RootElement.GetProperty("metadata").GetProperty("uid").GetString() != Required(spec, "kubernetes.route_uid")) return false;
        var body = ingress.RootElement.GetProperty("spec");
        List<JsonElement> backends = [];
        if (body.TryGetProperty("rules", out var rules))
            foreach (var rule in rules.EnumerateArray())
                if (rule.TryGetProperty("host", out var host) && host.GetString() == Required(spec, "kubernetes.route_host") && rule.TryGetProperty("http", out var http))
                    foreach (var path in http.GetProperty("paths").EnumerateArray())
                        if (path.GetProperty("path").GetString() == Required(spec, "kubernetes.route_path") &&
                            path.GetProperty("pathType").GetString() == Required(spec, "kubernetes.route_path_type")) backends.Add(path.GetProperty("backend"));
        var selectedBackend = backends.Count == 1 && backends[0].TryGetProperty("service", out var target) &&
            target.GetProperty("name").GetString() == Required(spec, "kubernetes.service_name") &&
            target.TryGetProperty("port", out var port) && (port.TryGetProperty("number", out var number) && number.GetInt32() == servicePort ||
                port.TryGetProperty("name", out var portName) && portName.GetString() == Required(spec, "kubernetes.service_port"));
        return selectedBackend && await VerifyPublicBindingAsync(ingress.RootElement, spec, publicUrl,
            requireCloudProof: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> VerifyPublicBindingAsync(JsonElement resource, DeployOperationSpec spec, Uri publicUrl,
        bool requireCloudProof, CancellationToken cancellationToken)
    {
        if (!resource.TryGetProperty("status", out var status) || !status.TryGetProperty("loadBalancer", out var lb) ||
            !lb.TryGetProperty("ingress", out var addresses) || addresses.ValueKind != JsonValueKind.Array || addresses.GetArrayLength() == 0) return false;
        var hasRegisteredDns = spec.Parameters.ContainsKey("kubernetes.dns_record_name");
        // Every observed route must agree. One matching address among conflicting load
        // balancers must not hide another possible serving target.
        foreach (var address in addresses.EnumerateArray())
        {
            if (address.TryGetProperty("hostname", out var hostname) && hostname.GetString() is { Length: > 0 } observed)
            {
                if (!requireCloudProof && !hasRegisteredDns && string.Equals(observed.TrimEnd('.'), publicUrl.IdnHost, StringComparison.OrdinalIgnoreCase)) continue;
                if (publicRouteVerifier is null || !await publicRouteVerifier.VerifyAsync(spec.Parameters, observed, cancellationToken).ConfigureAwait(false)) return false;
            }
            else if (!requireCloudProof && !hasRegisteredDns && address.TryGetProperty("ip", out var ip) && ip.GetString() == publicUrl.IdnHost) continue;
            else return false;
        }
        return true;
    }

    private static bool PortMatches(JsonElement port, string expected) => port.ValueKind == JsonValueKind.Number
        ? port.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) == expected : port.GetString() == expected;

    private static bool OwnedBy(JsonElement resource, string uid) => resource.GetProperty("metadata").TryGetProperty("ownerReferences", out var owners) &&
        owners.EnumerateArray().Any(owner => owner.GetProperty("uid").GetString() == uid && owner.TryGetProperty("controller", out var controller) && controller.GetBoolean());
    private static long Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var number) ? number.GetInt64() : 0;
    private static async Task<JsonDocument> ReadDeploymentAsync(BoundKubernetesConnection connection, DeployOperationSpec spec, CancellationToken cancellationToken)
    {
        using var response = await connection.SendAsync(HttpMethod.Get, DeploymentPath(spec), null, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }
    private static DeploymentSnapshot ReadSnapshot(JsonElement deployment, DeployOperationSpec spec)
    {
        var metadata = deployment.GetProperty("metadata");
        var uid = metadata.GetProperty("uid").GetString()!;
        if (uid != Required(spec, "kubernetes.deployment_uid")) throw new InvalidOperationException("The registered Deployment was replaced.");
        var containers = deployment.GetProperty("spec").GetProperty("template").GetProperty("spec").GetProperty("containers").EnumerateArray().ToArray();
        var indexes = Enumerable.Range(0, containers.Length).Where(i => containers[i].GetProperty("name").GetString() == Required(spec, "kubernetes.container_name")).ToArray();
        if (indexes.Length != 1) throw new InvalidOperationException("The registered Deployment container is unavailable or ambiguous.");
        return new(uid, metadata.GetProperty("resourceVersion").GetString()!, metadata.GetProperty("generation").GetInt64(), indexes[0], containers[indexes[0]].GetProperty("image").GetString()!);
    }
    private async Task RequireRegisteredTargetAsync(DeployOperationSpec spec, CancellationToken cancellationToken)
    {
        var registered = await targets.GetAsync(spec.TargetId, cancellationToken).ConfigureAwait(false);
        if (registered is null || registered.TargetId != spec.TargetId || registered.TargetKind != TargetKind ||
            registered.Backend != BackendName || registered.Environment != spec.Environment || spec.TargetKind != TargetKind || spec.Backend != BackendName)
            throw new InvalidOperationException("The native Deployment target is no longer registered with this authority.");
        static bool Protected(string key) => key.StartsWith("kubernetes.", StringComparison.Ordinal) || key == "honua.runtime_kind" ||
            key.StartsWith("telemetry.healthz.", StringComparison.Ordinal) || key.StartsWith("telemetry.golden_query.", StringComparison.Ordinal) ||
            key.StartsWith("rollback.functional_query.", StringComparison.Ordinal);
        foreach (var key in registered.Parameters.Keys.Concat(spec.Parameters.Keys).Where(Protected).Distinct(StringComparer.Ordinal))
            if (!registered.Parameters.TryGetValue(key, out var expected) || !spec.Parameters.TryGetValue(key, out var actual) || actual != expected)
                throw new InvalidOperationException("Registered Kubernetes identity and routing cannot be overridden or changed after review.");
    }

    private static string DeploymentPath(DeployOperationSpec spec) => $"/apis/apps/v1/namespaces/{Uri.EscapeDataString(Required(spec, "kubernetes.namespace"))}/deployments/{Uri.EscapeDataString(Required(spec, "kubernetes.deployment_name"))}";
    private static string ProviderId(DeployOperationSpec spec) => Required(spec, "kubernetes.deployment_uid");
    private static string Required(DeployOperationSpec spec, string key) => BoundKubernetesConnectionFactory.Required(spec.Parameters, key);
    private static DeployOperationSpec RequiredSpec(WorkflowOperationRecord operation) => operation.Deploy ?? throw new InvalidOperationException("Deploy metadata is required.");
    internal static bool IsImmutableImage(string? image) => image is not null && Regex.IsMatch(image, @"^[^\s@]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private sealed record DeploymentSnapshot(string Uid, string ResourceVersion, long Generation, int ContainerIndex, string Image);
}
