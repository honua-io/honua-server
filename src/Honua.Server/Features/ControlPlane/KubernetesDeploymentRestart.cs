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

    public static byte[] Serialize(DateTimeOffset restartedAt)
    {
        var timestamp = restartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
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
        var patch = KubernetesRestartPatch.Serialize(restartedAt);
        var path = $"/apis/apps/v1/namespaces/{Uri.EscapeDataString(namespaceName)}/deployments/{Uri.EscapeDataString(deploymentName)}";
        using var request = await requestFactory.CreateRequestAsync(
                HttpMethod.Patch,
                path,
                patch,
                cancellationToken,
                KubernetesRestartPatch.StrategicMergeContentType)
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

    private static partial class Log
    {
        [LoggerMessage(9114, LogLevel.Warning, "Kubernetes deployment restart failed with status {StatusCode}")]
        public static partial void ApiFailed(ILogger logger, int statusCode);
    }
}
