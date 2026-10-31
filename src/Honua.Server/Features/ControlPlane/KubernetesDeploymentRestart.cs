// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Honua.ControlPlane;

/// <summary>
/// Restarts one apps/v1 Deployment by patching only the restartedAt annotation.
/// The patch does not set a container image.
/// </summary>
internal interface IKubernetesWorkloadRestart
{
    Task RestartDeploymentAsync(
        string namespaceName,
        string deploymentName,
        DateTimeOffset restartedAt,
        CancellationToken cancellationToken = default);
}

internal static class KubernetesRestartPatch
{
    public const string Annotation = "kubectl.kubernetes.io/restartedAt";

    public const string StrategicMergeContentType = "application/strategic-merge-patch+json";

    public static byte[] Serialize(DateTimeOffset restartedAt, string resourceVersion)
    {
        var timestamp = restartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            writer.WriteString("resourceVersion", resourceVersion);
            writer.WriteEndObject();
            writer.WritePropertyName("spec");
            writer.WriteStartObject();
            writer.WritePropertyName("template");
            writer.WriteStartObject();
            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            writer.WritePropertyName("annotations");
            writer.WriteStartObject();
            writer.WriteString(Annotation, timestamp);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static string ValidateAndGetResourceVersion(JsonElement deployment)
    {
        if (!deployment.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("resourceVersion", out var version) ||
            version.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(version.GetString()) ||
            !deployment.TryGetProperty("spec", out var spec) ||
            !spec.TryGetProperty("template", out var template) ||
            !template.TryGetProperty("spec", out var podSpec) ||
            !podSpec.TryGetProperty("containers", out var containers) ||
            containers.ValueKind != JsonValueKind.Array || containers.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Kubernetes deployment template could not be verified.");
        }

        ValidateImages(containers);
        if (podSpec.TryGetProperty("initContainers", out var initContainers))
        {
            if (initContainers.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Kubernetes deployment template could not be verified.");
            }

            ValidateImages(initContainers);
        }

        return version.GetString()!;
    }

    private static void ValidateImages(JsonElement containers)
    {
        foreach (var container in containers.EnumerateArray())
        {
            if (!container.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.String ||
                !IsImmutableImage(image.GetString()))
            {
                throw new InvalidOperationException("Kubernetes restart requires immutable container images.");
            }
        }
    }

    private static bool IsImmutableImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return false;
        var index = image.LastIndexOf("@sha256:", StringComparison.Ordinal);
        if (index <= 0 || image.Length != index + 8 + 64) return false;
        foreach (var c in image.AsSpan(index + 8))
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        }

        return true;
    }

}

internal sealed partial class KubernetesDeploymentRestartClient(
    IHttpClientFactory httpClientFactory,
    KubernetesApiRequestFactory requestFactory,
    ILogger<KubernetesDeploymentRestartClient> logger) : IKubernetesWorkloadRestart
{
    public async Task RestartDeploymentAsync(
        string namespaceName,
        string deploymentName,
        DateTimeOffset restartedAt,
        CancellationToken cancellationToken = default)
    {
        var path = $"/apis/apps/v1/namespaces/{Uri.EscapeDataString(namespaceName)}/deployments/{Uri.EscapeDataString(deploymentName)}";
        using var client = httpClientFactory.CreateClient(KubernetesJobClient.HttpClientName);
        using var read = await requestFactory.CreateRequestAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        using var current = await client.SendAsync(read, cancellationToken).ConfigureAwait(false);
        if (current.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("Kubernetes deployment was not found.");
        }

        if (!current.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Kubernetes deployment request failed.");
        }

        await using var stream = await current.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var resourceVersion = KubernetesRestartPatch.ValidateAndGetResourceVersion(document.RootElement);
        var patch = KubernetesRestartPatch.Serialize(restartedAt, resourceVersion);
        using var request = await requestFactory.CreateRequestAsync(
                HttpMethod.Patch,
                path,
                patch,
                cancellationToken,
                KubernetesRestartPatch.StrategicMergeContentType)
            .ConfigureAwait(false);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("Kubernetes deployment was not found.");
        }

        if (!response.IsSuccessStatusCode)
        {
            Log.ApiFailed(logger, (int)response.StatusCode);
            throw new InvalidOperationException("Kubernetes deployment request failed.");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9114, LogLevel.Warning, "Kubernetes deployment restart failed with status {StatusCode}")]
        public static partial void ApiFailed(ILogger logger, int statusCode);
    }
}
