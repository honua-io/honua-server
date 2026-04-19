// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Low-level JSON helpers for Kubernetes Job and Pod payloads. Uses
/// <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/> directly so the
/// client stays AOT- and trim-safe without pulling in the official Kubernetes
/// client or reflection-based serialization.
/// </summary>
internal static class KubernetesJobManifestSerializer
{
    /// <summary>
    /// Builds a minimal <c>batch/v1</c> Job manifest payload.
    /// </summary>
    public static byte[] SerializeJobManifest(KubernetesJobManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("apiVersion", "batch/v1");
            writer.WriteString("kind", "Job");

            writer.WriteStartObject("metadata");
            writer.WriteString("name", manifest.Name);
            writer.WriteString("namespace", manifest.Namespace);
            WriteStringMap(writer, "labels", manifest.Labels);
            WriteStringMap(writer, "annotations", manifest.Annotations);
            writer.WriteEndObject();

            writer.WriteStartObject("spec");
            writer.WriteNumber("backoffLimit", 0);
            if (manifest.TtlSecondsAfterFinished.HasValue)
            {
                writer.WriteNumber("ttlSecondsAfterFinished", manifest.TtlSecondsAfterFinished.Value);
            }

            if (manifest.ActiveDeadlineSeconds.HasValue)
            {
                writer.WriteNumber("activeDeadlineSeconds", manifest.ActiveDeadlineSeconds.Value);
            }

            writer.WriteStartObject("template");
            writer.WriteStartObject("metadata");
            WriteStringMap(writer, "labels", manifest.Labels);
            writer.WriteEndObject();

            writer.WriteStartObject("spec");
            writer.WriteString("restartPolicy", "Never");
            if (!string.IsNullOrWhiteSpace(manifest.ServiceAccount))
            {
                writer.WriteString("serviceAccountName", manifest.ServiceAccount);
            }

            WriteStringMap(writer, "nodeSelector", manifest.NodeSelector);

            if (manifest.ImagePullSecrets.Count > 0)
            {
                writer.WriteStartArray("imagePullSecrets");
                foreach (var secret in manifest.ImagePullSecrets)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", secret);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteStartArray("containers");
            writer.WriteStartObject();
            writer.WriteString("name", "worker");
            writer.WriteString("image", manifest.Image);
            if (!string.IsNullOrWhiteSpace(manifest.ImagePullPolicy))
            {
                writer.WriteString("imagePullPolicy", manifest.ImagePullPolicy);
            }

            if (manifest.EnvironmentVariables.Count > 0)
            {
                writer.WriteStartArray("env");
                foreach (var env in manifest.EnvironmentVariables)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", env.Key);
                    writer.WriteString("value", env.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            WriteResources(writer, manifest);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteEndObject(); // pod spec
            writer.WriteEndObject(); // template
            writer.WriteEndObject(); // job spec
            writer.WriteEndObject(); // root
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Builds the request body for a Job DELETE call with cascade propagation.
    /// </summary>
    public static byte[] SerializeDeleteOptions(string propagationPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propagationPolicy);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            // Kubernetes DeleteOptions request bodies carry apiVersion "v1" (not
            // "meta/v1"); the API server rejects unrecognized API versions for
            // DeleteOptions.
            writer.WriteString("apiVersion", "v1");
            writer.WriteString("kind", "DeleteOptions");
            writer.WriteString("propagationPolicy", propagationPolicy);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Extracts the lifecycle-relevant fields from a Job API response payload.
    /// </summary>
    public static KubernetesJobStatusSnapshot ParseJobStatus(JsonElement root)
    {
        string? uid = null;
        string? ns = null;
        string? name = null;
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            uid = metadata.TryGetProperty("uid", out var uidEl) ? uidEl.GetString() : null;
            ns = metadata.TryGetProperty("namespace", out var nsEl) ? nsEl.GetString() : null;
            name = metadata.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        }

        var active = 0;
        var succeeded = 0;
        var failed = 0;
        var completeCondition = false;
        var failedCondition = false;
        string? terminalReason = null;
        string? terminalMessage = null;

        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            active = ReadOptionalInt(status, "active");
            succeeded = ReadOptionalInt(status, "succeeded");
            failed = ReadOptionalInt(status, "failed");

            if (status.TryGetProperty("conditions", out var conditions) &&
                conditions.ValueKind == JsonValueKind.Array)
            {
                foreach (var condition in conditions.EnumerateArray())
                {
                    if (condition.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var type = condition.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                    var conditionStatus = condition.TryGetProperty("status", out var statusEl)
                        ? statusEl.GetString()
                        : null;

                    var isTrue = string.Equals(conditionStatus, "True", StringComparison.OrdinalIgnoreCase);
                    if (!isTrue)
                    {
                        continue;
                    }

                    if (string.Equals(type, "Complete", StringComparison.OrdinalIgnoreCase))
                    {
                        completeCondition = true;
                    }
                    else if (string.Equals(type, "Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        failedCondition = true;
                        terminalReason = condition.TryGetProperty("reason", out var reasonEl)
                            ? reasonEl.GetString()
                            : terminalReason;
                        terminalMessage = condition.TryGetProperty("message", out var messageEl)
                            ? messageEl.GetString()
                            : terminalMessage;
                    }
                }
            }
        }

        return new KubernetesJobStatusSnapshot
        {
            Uid = uid,
            Namespace = ns,
            Name = name,
            Active = active,
            Succeeded = succeeded,
            Failed = failed,
            CompleteCondition = completeCondition,
            FailedCondition = failedCondition,
            TerminalReason = terminalReason,
            TerminalMessage = terminalMessage
        };
    }

    /// <summary>
    /// Extracts per-pod lifecycle state from a pod list response.
    /// </summary>
    public static IReadOnlyList<KubernetesPodStatusSnapshot> ParsePodList(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<KubernetesPodStatusSnapshot>();
        }

        var result = new List<KubernetesPodStatusSnapshot>();
        foreach (var pod in items.EnumerateArray())
        {
            var snapshot = ParsePod(pod);
            if (snapshot != null)
            {
                result.Add(snapshot);
            }
        }

        return result;
    }

    private static KubernetesPodStatusSnapshot? ParsePod(JsonElement pod)
    {
        if (pod.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = pod.TryGetProperty("metadata", out var metadata) &&
                   metadata.ValueKind == JsonValueKind.Object &&
                   metadata.TryGetProperty("name", out var nameEl)
            ? nameEl.GetString()
            : null;

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        string? phase = null;
        string? reason = null;
        string? message = null;
        string? containerTerminationReason = null;
        string? containerTerminationMessage = null;
        int? containerExitCode = null;

        if (pod.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            phase = status.TryGetProperty("phase", out var phaseEl) ? phaseEl.GetString() : null;
            reason = status.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() : null;
            message = status.TryGetProperty("message", out var messageEl) ? messageEl.GetString() : null;

            if (status.TryGetProperty("containerStatuses", out var containerStatuses) &&
                containerStatuses.ValueKind == JsonValueKind.Array)
            {
                foreach (var containerStatus in containerStatuses.EnumerateArray())
                {
                    if (containerStatus.ValueKind != JsonValueKind.Object ||
                        !containerStatus.TryGetProperty("state", out var state) ||
                        state.ValueKind != JsonValueKind.Object ||
                        !state.TryGetProperty("terminated", out var terminated) ||
                        terminated.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    containerTerminationReason = terminated.TryGetProperty("reason", out var reasonEl2)
                        ? reasonEl2.GetString()
                        : containerTerminationReason;
                    containerTerminationMessage = terminated.TryGetProperty("message", out var messageEl2)
                        ? messageEl2.GetString()
                        : containerTerminationMessage;
                    if (terminated.TryGetProperty("exitCode", out var exitCodeEl) &&
                        exitCodeEl.ValueKind == JsonValueKind.Number &&
                        exitCodeEl.TryGetInt32(out var exitCode))
                    {
                        containerExitCode = exitCode;
                    }

                    break;
                }
            }
        }

        return new KubernetesPodStatusSnapshot
        {
            Name = name!,
            Phase = phase,
            Reason = reason,
            Message = message,
            ContainerTerminationReason = containerTerminationReason,
            ContainerTerminationMessage = containerTerminationMessage,
            ContainerExitCode = containerExitCode
        };
    }

    private static void WriteStringMap(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyDictionary<string, string>? values)
    {
        if (values == null || values.Count == 0)
        {
            return;
        }

        writer.WriteStartObject(propertyName);
        foreach (var entry in values)
        {
            writer.WriteString(entry.Key, entry.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteResources(Utf8JsonWriter writer, KubernetesJobManifest manifest)
    {
        var hasRequests = !string.IsNullOrWhiteSpace(manifest.CpuRequest) ||
                          !string.IsNullOrWhiteSpace(manifest.MemoryRequest);
        var hasLimits = !string.IsNullOrWhiteSpace(manifest.CpuLimit) ||
                        !string.IsNullOrWhiteSpace(manifest.MemoryLimit);

        if (!hasRequests && !hasLimits)
        {
            return;
        }

        writer.WriteStartObject("resources");
        if (hasRequests)
        {
            writer.WriteStartObject("requests");
            if (!string.IsNullOrWhiteSpace(manifest.CpuRequest))
            {
                writer.WriteString("cpu", manifest.CpuRequest);
            }

            if (!string.IsNullOrWhiteSpace(manifest.MemoryRequest))
            {
                writer.WriteString("memory", manifest.MemoryRequest);
            }

            writer.WriteEndObject();
        }

        if (hasLimits)
        {
            writer.WriteStartObject("limits");
            if (!string.IsNullOrWhiteSpace(manifest.CpuLimit))
            {
                writer.WriteString("cpu", manifest.CpuLimit);
            }

            if (!string.IsNullOrWhiteSpace(manifest.MemoryLimit))
            {
                writer.WriteString("memory", manifest.MemoryLimit);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static int ReadOptionalInt(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element))
        {
            return 0;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : 0;
    }
}
