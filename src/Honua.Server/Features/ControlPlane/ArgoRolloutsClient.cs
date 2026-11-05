// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;

namespace Honua.ControlPlane;

internal enum ArgoRolloutPhase
{
    Unknown,

    Progressing,

    Paused,

    Healthy,

    Degraded
}

internal sealed record ArgoRolloutState
{
    public required string Name { get; init; }

    public ArgoRolloutPhase Phase { get; init; }

    public string? Message { get; init; }

    public int? CanaryWeight { get; init; }

    public bool IsPaused { get; init; }

    public bool IsAborted { get; init; }

    public string? CurrentPodHash { get; init; }

    public string? StableRevisionHash { get; init; }

    public string? PodTemplateImage { get; init; }
}

internal interface IArgoRolloutsClient
{
    Task<ArgoRolloutState?> GetRolloutAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default);

    Task<ArgoRolloutState> SetImageAsync(
        string @namespace,
        string name,
        string containerName,
        string image,
        CancellationToken cancellationToken = default);

    Task<ArgoRolloutState> PromoteAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default);

    Task<ArgoRolloutState> AbortAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default);
}

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
        catch (Exception readEx) when (readEx is not OutOfMemoryException)
        {
            // Intentional broad catch: this is a best-effort read of the failed response body
            // purely for diagnostic logging; if the body can't be read for any reason, fall
            // back to a placeholder rather than letting a secondary failure mask the original
            // API error being reported below.
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

internal static class ArgoRolloutsPatchSerializer
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

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

    // The merge patch unsets spec.paused so a spec-level pause does not re-pause the rollout after promotion.
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

        return containers.EnumerateArray()
            .Select(container =>
                container.ValueKind == JsonValueKind.Object &&
                container.TryGetProperty("image", out var imageElement) &&
                imageElement.ValueKind == JsonValueKind.String
                    ? imageElement.GetString()
                    : null)
            .FirstOrDefault(image => image is not null);
    }
}
