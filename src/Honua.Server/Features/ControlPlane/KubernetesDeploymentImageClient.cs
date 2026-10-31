// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;

namespace Honua.ControlPlane;

internal sealed record KubernetesDeploymentImageState
{
    public required string Name { get; init; }

    public int Replicas { get; init; }

    public int TotalReplicas { get; init; }

    public int ReadyReplicas { get; init; }

    public int UpdatedReplicas { get; init; }

    /// <summary>Deployment <c>metadata.generation</c>. Zero when the API omitted it.</summary>
    public long Generation { get; init; }

    /// <summary>Deployment <c>status.observedGeneration</c>. Zero when the controller has not reported it.</summary>
    public long ObservedGeneration { get; init; }

    public required IReadOnlyDictionary<string, string> ContainerImages { get; init; }
}

/// <summary>
/// Reads and sets one container image on an apps/v1 Deployment. The patch is a
/// strategic merge of that container only.
/// </summary>
internal interface IKubernetesDeploymentImageClient
{
    Task<KubernetesDeploymentImageState?> GetAsync(
        string namespaceName,
        string deploymentName,
        CancellationToken cancellationToken = default);

    Task SetContainerImageAsync(
        string namespaceName,
        string deploymentName,
        string containerName,
        string image,
        CancellationToken cancellationToken = default);
}

internal static class KubernetesDeploymentImagePatch
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    public const string StrategicMergeContentType = "application/strategic-merge-patch+json";

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

    public static KubernetesDeploymentImageState Parse(JsonElement root, string expectedName)
    {
        var name = expectedName;
        long generation = 0;
        if (root.TryGetProperty("metadata", out var metadata))
        {
            generation = ReadInt64(metadata, "generation");
            if (metadata.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                name = nameElement.GetString()!;
            }
        }

        var replicas = 0;
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("spec", out var spec))
        {
            if (spec.TryGetProperty("replicas", out var replicasElement) &&
                replicasElement.TryGetInt32(out var parsedReplicas))
            {
                replicas = parsedReplicas;
            }

            if (spec.TryGetProperty("template", out var template) &&
                template.TryGetProperty("spec", out var podSpec) &&
                podSpec.TryGetProperty("containers", out var containers) &&
                containers.ValueKind == JsonValueKind.Array)
            {
                foreach (var container in containers.EnumerateArray())
                {
                    if (!container.TryGetProperty("name", out var containerNameElement) ||
                        containerNameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var containerName = containerNameElement.GetString();
                    if (string.IsNullOrWhiteSpace(containerName) ||
                        !container.TryGetProperty("image", out var imageElement) ||
                        imageElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    images[containerName] = imageElement.GetString() ?? string.Empty;
                }
            }
        }

        var total = 0;
        var ready = 0;
        var updated = 0;
        long observedGeneration = 0;
        if (root.TryGetProperty("status", out var status))
        {
            observedGeneration = ReadInt64(status, "observedGeneration");
            if (status.TryGetProperty("replicas", out var totalElement) && totalElement.TryGetInt32(out var parsedTotal))
            {
                total = parsedTotal;
            }
            if (status.TryGetProperty("readyReplicas", out var readyElement) &&
                readyElement.TryGetInt32(out var parsedReady))
            {
                ready = parsedReady;
            }

            if (status.TryGetProperty("updatedReplicas", out var updatedElement) &&
                updatedElement.TryGetInt32(out var parsedUpdated))
            {
                updated = parsedUpdated;
            }
        }

        return new KubernetesDeploymentImageState
        {
            Name = name,
            Replicas = replicas,
            TotalReplicas = total,
            ReadyReplicas = ready,
            UpdatedReplicas = updated,
            Generation = generation,
            ObservedGeneration = observedGeneration,
            ContainerImages = images
        };
    }

    private static long ReadInt64(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var element) && element.TryGetInt64(out var value) ? value : 0;
}

internal sealed partial class KubernetesDeploymentImageClient(
    IHttpClientFactory httpClientFactory,
    KubernetesApiRequestFactory requestFactory,
    ILogger<KubernetesDeploymentImageClient> logger) : IKubernetesDeploymentImageClient
{
    public async Task<KubernetesDeploymentImageState?> GetAsync(
        string namespaceName,
        string deploymentName,
        CancellationToken cancellationToken = default)
    {
        using var request = await requestFactory.CreateRequestAsync(
                HttpMethod.Get,
                DeploymentPath(namespaceName, deploymentName),
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
            Log.ApiFailed(logger, (int)response.StatusCode);
            throw new InvalidOperationException("Kubernetes deployment request failed.");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return KubernetesDeploymentImagePatch.Parse(document.RootElement, deploymentName);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Kubernetes deployment response could not be read.");
        }
    }

    public async Task SetContainerImageAsync(
        string namespaceName,
        string deploymentName,
        string containerName,
        string image,
        CancellationToken cancellationToken = default)
    {
        var patch = KubernetesDeploymentImagePatch.SerializeSetImage(containerName, image);
        using var request = await requestFactory.CreateRequestAsync(
                HttpMethod.Patch,
                DeploymentPath(namespaceName, deploymentName),
                patch,
                cancellationToken,
                KubernetesDeploymentImagePatch.StrategicMergeContentType)
            .ConfigureAwait(false);
        using var client = httpClientFactory.CreateClient(KubernetesJobClient.HttpClientName);
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

    internal static string DeploymentPath(string namespaceName, string deploymentName)
        => $"/apis/apps/v1/namespaces/{Uri.EscapeDataString(namespaceName)}/deployments/{Uri.EscapeDataString(deploymentName)}";

    private static partial class Log
    {
        [LoggerMessage(9110, LogLevel.Warning, "Kubernetes deployment API request failed with status {StatusCode}")]
        public static partial void ApiFailed(ILogger logger, int statusCode);
    }
}
