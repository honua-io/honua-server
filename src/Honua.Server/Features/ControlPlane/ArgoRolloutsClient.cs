// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;

namespace Honua.ControlPlane;

/// <summary>
/// Argo Rollouts lifecycle phase as reported by <c>.status.phase</c> on the Rollout
/// custom resource. Mirrors the canonical Argo Rollouts phases so the deploy backend
/// can map them onto the control-plane operation lifecycle without taking a dependency
/// on the Argo client libraries.
/// </summary>
internal enum ArgoRolloutPhase
{
    /// <summary>
    /// Phase could not be determined from the resource status (missing or unknown value).
    /// </summary>
    Unknown,

    /// <summary>
    /// The rollout is actively progressing toward the desired ReplicaSet.
    /// </summary>
    Progressing,

    /// <summary>
    /// The rollout is paused at a canary step (for example awaiting a manual or
    /// telemetry-gated promotion).
    /// </summary>
    Paused,

    /// <summary>
    /// The rollout fully converged and is serving the desired revision at 100% weight.
    /// </summary>
    Healthy,

    /// <summary>
    /// The rollout failed analysis, progress deadline, or was aborted and is degraded.
    /// </summary>
    Degraded
}

/// <summary>
/// Read-only snapshot of an Argo Rollouts <c>Rollout</c> custom resource. Only the
/// fields the deploy backend needs to map rollout status into the control-plane
/// operation lifecycle are modeled.
/// </summary>
internal sealed record ArgoRolloutState
{
    /// <summary>
    /// Rollout name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Reported lifecycle phase from <c>.status.phase</c>.
    /// </summary>
    public ArgoRolloutPhase Phase { get; init; }

    /// <summary>
    /// Operator-facing message from <c>.status.message</c>, when present.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Canary traffic weight currently routed to the new revision
    /// (<c>.status.canary.weights.canary.weight</c>). Null when the rollout is not
    /// reporting a weighted canary (for example a blue-green strategy or a rollout that
    /// has not started shifting traffic).
    /// </summary>
    public int? CanaryWeight { get; init; }

    /// <summary>
    /// Whether the rollout currently has at least one pause condition
    /// (<c>.status.pauseConditions</c>) — typically an unweighted or weighted canary
    /// step that is waiting for promotion.
    /// </summary>
    public bool IsPaused { get; init; }

    /// <summary>
    /// Whether the rollout is in an aborted state (<c>.status.abort</c>), which drives
    /// it back to the stable revision.
    /// </summary>
    public bool IsAborted { get; init; }

    /// <summary>
    /// Pod-template-hash of the current desired revision (<c>.status.currentPodHash</c>).
    /// </summary>
    public string? CurrentPodHash { get; init; }

    /// <summary>
    /// Stable ReplicaSet pod-template-hash (<c>.status.stableRS</c>). When this matches
    /// <see cref="CurrentPodHash"/> the desired revision has become the stable revision.
    /// </summary>
    public string? StableRevisionHash { get; init; }

    /// <summary>
    /// Container image currently set on the rollout pod template for the configured
    /// container (<c>spec.template.spec.containers[name].image</c>). Used to confirm the
    /// desired image was applied at submission time.
    /// </summary>
    public string? PodTemplateImage { get; init; }
}

/// <summary>
/// Narrow REST adapter over the Argo Rollouts <c>argoproj.io/v1alpha1</c> Rollout API.
/// Only the verbs the <see cref="KubernetesArgoRolloutsDeployBackend"/> needs are
/// modeled — get status, set the canary image (start), promote (clear pause), and abort
/// (rollback) — so the surface stays AOT-friendly and free of the Argo client libraries.
/// </summary>
internal interface IArgoRolloutsClient
{
    /// <summary>
    /// Fetches the current Rollout status. Returns null when the Rollout does not exist
    /// (404) so the backend can surface a configuration error rather than throwing.
    /// </summary>
    Task<ArgoRolloutState?> GetRolloutAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the container image on the Rollout pod template, which triggers Argo Rollouts
    /// to begin the configured canary steps. Uses a strategic-merge patch keyed by the
    /// container name so sibling containers and pod-template fields are preserved.
    /// </summary>
    Task<ArgoRolloutState> SetImageAsync(
        string @namespace,
        string name,
        string containerName,
        string image,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes a paused Rollout by clearing its pause conditions on the status
    /// subresource and unsetting <c>spec.paused</c>. Argo Rollouts then advances to the
    /// next canary step (or to 100% when the final step is reached). Mirrors the
    /// <c>kubectl-argo-rollouts promote</c> behavior.
    /// </summary>
    Task<ArgoRolloutState> PromoteAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts a Rollout by setting <c>status.abort=true</c>, which drives traffic back to
    /// the stable revision. Mirrors the <c>kubectl-argo-rollouts abort</c> behavior.
    /// </summary>
    Task<ArgoRolloutState> AbortAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Raw-HTTP implementation of <see cref="IArgoRolloutsClient"/>. Reuses the shared
/// Kubernetes API request factory (in-cluster service-account or configured
/// out-of-cluster auth) and the <c>control-plane-kubernetes</c> HTTP client so it shares
/// one credential-resolution and TLS-trust path with <see cref="KubernetesJobClient"/>.
/// </summary>
internal sealed partial class ArgoRolloutsClient(
    IHttpClientFactory httpClientFactory,
    KubernetesApiRequestFactory requestFactory,
    ILogger<ArgoRolloutsClient> logger) : IArgoRolloutsClient
{
    private const string StrategicMergePatchContentType = "application/strategic-merge-patch+json";
    private const string MergePatchContentType = "application/merge-patch+json";

    public async Task<ArgoRolloutState?> GetRolloutAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var request = await requestFactory.CreateRequestAsync(
                HttpMethod.Get,
                BuildRolloutPath(@namespace, name),
                payload: null,
                cancellationToken)
            .ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient(KubernetesJobClient.HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, "get Rollout", cancellationToken).ConfigureAwait(false);
        }

        return await ParseRolloutAsync(response, name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArgoRolloutState> SetImageAsync(
        string @namespace,
        string name,
        string containerName,
        string image,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        var patch = ArgoRolloutsPatchSerializer.SerializeSetImage(containerName, image);
        return await PatchRolloutAsync(
                @namespace,
                name,
                BuildRolloutPath(@namespace, name),
                patch,
                StrategicMergePatchContentType,
                "set Rollout image",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ArgoRolloutState> PromoteAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Clear the pause on the status subresource so the controller advances to the
        // next canary step, and unset spec.paused so a spec-level pause does not re-pause
        // the rollout. Mirrors the kubectl-argo-rollouts promote contract.
        await PatchRolloutAsync(
                @namespace,
                name,
                BuildRolloutStatusPath(@namespace, name),
                ArgoRolloutsPatchSerializer.SerializeClearPauseStatus(),
                MergePatchContentType,
                "promote Rollout (status)",
                cancellationToken)
            .ConfigureAwait(false);

        return await PatchRolloutAsync(
                @namespace,
                name,
                BuildRolloutPath(@namespace, name),
                ArgoRolloutsPatchSerializer.SerializeUnpauseSpec(),
                MergePatchContentType,
                "promote Rollout (spec)",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ArgoRolloutState> AbortAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Setting status.abort=true reverts the rollout to the stable revision. Argo
        // Rollouts owns the abort field on the status subresource.
        return await PatchRolloutAsync(
                @namespace,
                name,
                BuildRolloutStatusPath(@namespace, name),
                ArgoRolloutsPatchSerializer.SerializeAbortStatus(),
                MergePatchContentType,
                "abort Rollout",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ArgoRolloutState> PatchRolloutAsync(
        string @namespace,
        string name,
        string path,
        byte[] patch,
        string contentType,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = await requestFactory.CreateRequestAsync(
                HttpMethod.Patch,
                path,
                patch,
                cancellationToken,
                contentType)
            .ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient(KubernetesJobClient.HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, operation, cancellationToken).ConfigureAwait(false);
        }

        return await ParseRolloutAsync(response, name, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildRolloutPath(string @namespace, string name)
        => $"/apis/argoproj.io/v1alpha1/namespaces/{Uri.EscapeDataString(@namespace)}/rollouts/{Uri.EscapeDataString(name)}";

    private static string BuildRolloutStatusPath(string @namespace, string name)
        => BuildRolloutPath(@namespace, name) + "/status";

    private static async Task<ArgoRolloutState> ParseRolloutAsync(
        HttpResponseMessage response,
        string name,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return ArgoRolloutsPatchSerializer.ParseRollout(document.RootElement, name);
    }

    private async Task ThrowApiErrorAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception readEx)
        {
            body = $"<unreadable response body: {readEx.Message}>";
        }

        Log.ArgoRolloutsApiError(logger, operation, (int)response.StatusCode, body);
        throw new HttpRequestException(
            $"Argo Rollouts API request to {operation} failed with status {(int)response.StatusCode}: {body}",
            inner: null,
            response.StatusCode);
    }

    private static partial class Log
    {
        [LoggerMessage(9100, LogLevel.Warning,
            "Argo Rollouts API call to {Operation} failed with status {StatusCode}: {Body}")]
        public static partial void ArgoRolloutsApiError(ILogger logger, string operation, int statusCode, string body);
    }
}

/// <summary>
/// Serializes the small set of Argo Rollouts patch bodies and parses Rollout status.
/// Lifted out of <see cref="ArgoRolloutsClient"/> so the patch/parse contract can be
/// unit-tested without an HTTP fake.
/// </summary>
internal static class ArgoRolloutsPatchSerializer
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>
    /// Builds a strategic-merge patch that sets the image on a named container in the
    /// Rollout pod template. The container name is the strategic-merge key so sibling
    /// containers are preserved.
    /// </summary>
    public static byte[] SerializeSetImage(string containerName, string image)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("spec");
            writer.WriteStartObject();
            writer.WritePropertyName("template");
            writer.WriteStartObject();
            writer.WritePropertyName("spec");
            writer.WriteStartObject();
            writer.WritePropertyName("containers");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("name", containerName);
            writer.WriteString("image", image);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Builds a merge patch for the status subresource that clears pause conditions and
    /// unsets the abort flag, advancing a paused rollout.
    /// </summary>
    public static byte[] SerializeClearPauseStatus()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("status");
            writer.WriteStartObject();
            writer.WriteNull("pauseConditions");
            writer.WriteBoolean("abort", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Builds a merge patch that unsets <c>spec.paused</c> so a spec-level pause does not
    /// re-pause the rollout after promotion.
    /// </summary>
    public static byte[] SerializeUnpauseSpec()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("spec");
            writer.WriteStartObject();
            writer.WriteBoolean("paused", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Builds a merge patch for the status subresource that sets <c>abort=true</c>,
    /// driving the rollout back to the stable revision.
    /// </summary>
    public static byte[] SerializeAbortStatus()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("status");
            writer.WriteStartObject();
            writer.WriteBoolean("abort", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Parses an Argo Rollouts custom-resource JSON document into the narrow
    /// <see cref="ArgoRolloutState"/> the deploy backend consumes.
    /// </summary>
    public static ArgoRolloutState ParseRollout(JsonElement root, string fallbackName)
    {
        var name = fallbackName;
        if (root.TryGetProperty("metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("name", out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String)
        {
            name = nameElement.GetString() ?? fallbackName;
        }

        var phase = ArgoRolloutPhase.Unknown;
        string? message = null;
        int? canaryWeight = null;
        var isPaused = false;
        var isAborted = false;
        string? currentPodHash = null;
        string? stableRevisionHash = null;

        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            if (status.TryGetProperty("phase", out var phaseElement) &&
                phaseElement.ValueKind == JsonValueKind.String)
            {
                phase = ParsePhase(phaseElement.GetString());
            }

            if (status.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString();
            }

            if (status.TryGetProperty("abort", out var abortElement) &&
                (abortElement.ValueKind == JsonValueKind.True || abortElement.ValueKind == JsonValueKind.False))
            {
                isAborted = abortElement.GetBoolean();
            }

            if (status.TryGetProperty("pauseConditions", out var pauseElement) &&
                pauseElement.ValueKind == JsonValueKind.Array)
            {
                isPaused = pauseElement.GetArrayLength() > 0;
            }

            if (status.TryGetProperty("currentPodHash", out var currentHashElement) &&
                currentHashElement.ValueKind == JsonValueKind.String)
            {
                currentPodHash = currentHashElement.GetString();
            }

            if (status.TryGetProperty("stableRS", out var stableElement) &&
                stableElement.ValueKind == JsonValueKind.String)
            {
                stableRevisionHash = stableElement.GetString();
            }

            canaryWeight = ParseCanaryWeight(status);
        }

        var podTemplateImage = ParsePodTemplateImage(root);

        return new ArgoRolloutState
        {
            Name = name,
            Phase = phase,
            Message = message,
            CanaryWeight = canaryWeight,
            IsPaused = isPaused,
            IsAborted = isAborted,
            CurrentPodHash = currentPodHash,
            StableRevisionHash = stableRevisionHash,
            PodTemplateImage = podTemplateImage
        };
    }

    private static ArgoRolloutPhase ParsePhase(string? phase)
        => phase switch
        {
            "Progressing" => ArgoRolloutPhase.Progressing,
            "Paused" => ArgoRolloutPhase.Paused,
            "Healthy" => ArgoRolloutPhase.Healthy,
            "Degraded" => ArgoRolloutPhase.Degraded,
            _ => ArgoRolloutPhase.Unknown
        };

    private static int? ParseCanaryWeight(JsonElement status)
    {
        // .status.canary.weights.canary.weight is the authoritative current canary share.
        if (!status.TryGetProperty("canary", out var canary) || canary.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!canary.TryGetProperty("weights", out var weights) || weights.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!weights.TryGetProperty("canary", out var canaryWeights) || canaryWeights.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (canaryWeights.TryGetProperty("weight", out var weightElement) &&
            weightElement.ValueKind == JsonValueKind.Number &&
            weightElement.TryGetInt32(out var weight))
        {
            return weight;
        }

        return null;
    }

    private static string? ParsePodTemplateImage(JsonElement root)
    {
        if (!root.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!spec.TryGetProperty("template", out var template) || template.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!template.TryGetProperty("spec", out var templateSpec) || templateSpec.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!templateSpec.TryGetProperty("containers", out var containers) ||
            containers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var container in containers.EnumerateArray())
        {
            if (container.ValueKind == JsonValueKind.Object &&
                container.TryGetProperty("image", out var imageElement) &&
                imageElement.ValueKind == JsonValueKind.String)
            {
                return imageElement.GetString();
            }
        }

        return null;
    }
}
