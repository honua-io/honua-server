// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Honua.ControlPlane;

internal sealed record KubernetesJobCreateResult
{
    public required HttpStatusCode StatusCode { get; init; }

    public bool AlreadyExists => StatusCode == HttpStatusCode.Conflict;

    public KubernetesJobStatusSnapshot? Snapshot { get; init; }
}

internal sealed record KubernetesJobFetchResult
{
    public required HttpStatusCode StatusCode { get; init; }

    public bool NotFound => StatusCode == HttpStatusCode.NotFound;

    public KubernetesJobStatusSnapshot? Snapshot { get; init; }
}

internal sealed record KubernetesJobDeleteResult
{
    public required HttpStatusCode StatusCode { get; init; }

    public bool NotFound => StatusCode == HttpStatusCode.NotFound;

    public KubernetesJobStatusSnapshot? Snapshot { get; init; }
}

internal interface IKubernetesJobClient
{
    Task<KubernetesJobCreateResult> CreateJobAsync(
        KubernetesJobManifest manifest,
        CancellationToken cancellationToken = default);

    Task<KubernetesJobFetchResult> GetJobAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default);

    Task<KubernetesJobDeleteResult> DeleteJobAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KubernetesPodStatusSnapshot>> ListPodsAsync(
        string @namespace,
        string labelSelector,
        CancellationToken cancellationToken = default);
}

internal sealed partial class KubernetesJobClient(
    IHttpClientFactory httpClientFactory,
    KubernetesApiRequestFactory requestFactory,
    ILogger<KubernetesJobClient> logger) : IKubernetesJobClient
{
    internal const string HttpClientName = "control-plane-kubernetes";

    private const string InClusterCaCertPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
    private const string InClusterNamespacePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    public async Task<KubernetesJobCreateResult> CreateJobAsync(
        KubernetesJobManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var body = KubernetesJobManifestSerializer.SerializeJobManifest(manifest);
        using var request = await CreateRequestAsync(
                HttpMethod.Post,
                $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(manifest.Namespace)}/jobs",
                body,
                cancellationToken).ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
        {
            await ThrowApiErrorAsync(response, "create Job", cancellationToken).ConfigureAwait(false);
        }

        KubernetesJobStatusSnapshot? snapshot = null;
        if (response.IsSuccessStatusCode)
        {
            snapshot = await ParseJobResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return new KubernetesJobCreateResult
        {
            StatusCode = response.StatusCode,
            Snapshot = snapshot
        };
    }

    public async Task<KubernetesJobFetchResult> GetJobAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var request = await CreateRequestAsync(
                HttpMethod.Get,
                $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(@namespace)}/jobs/{Uri.EscapeDataString(name)}",
                payload: null,
                cancellationToken).ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new KubernetesJobFetchResult { StatusCode = response.StatusCode };
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, "get Job", cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await ParseJobResponseAsync(response, cancellationToken).ConfigureAwait(false);
        return new KubernetesJobFetchResult
        {
            StatusCode = response.StatusCode,
            Snapshot = snapshot
        };
    }

    public async Task<KubernetesJobDeleteResult> DeleteJobAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var payload = KubernetesJobManifestSerializer.SerializeDeleteOptions("Background");
        using var request = await CreateRequestAsync(
                HttpMethod.Delete,
                $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(@namespace)}/jobs/{Uri.EscapeDataString(name)}",
                payload,
                cancellationToken).ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new KubernetesJobDeleteResult { StatusCode = response.StatusCode };
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, "delete Job", cancellationToken).ConfigureAwait(false);
        }

        // Kubernetes may return either the Job object or a bare Status kind on DELETE.
        // When the body represents the Job, ParseJobStatus reads its terminal conditions
        // so the adapter can preserve Succeeded/Failed outcomes that occurred between
        // the pre-delete GET and this DELETE. A Status body yields an empty snapshot
        // (no conditions, no pod counts) which the caller treats as non-terminal.
        var snapshot = await TryParseJobResponseAsync(response, cancellationToken).ConfigureAwait(false);
        return new KubernetesJobDeleteResult
        {
            StatusCode = response.StatusCode,
            Snapshot = snapshot
        };
    }

    public async Task<IReadOnlyList<KubernetesPodStatusSnapshot>> ListPodsAsync(
        string @namespace,
        string labelSelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(labelSelector);

        var path = $"/api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/pods?labelSelector={Uri.EscapeDataString(labelSelector)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, payload: null, cancellationToken)
            .ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<KubernetesPodStatusSnapshot>();
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, "list Pods", cancellationToken).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return KubernetesJobManifestSerializer.ParsePodList(document.RootElement);
    }

    private Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string relativeUrl,
        byte[]? payload,
        CancellationToken cancellationToken)
        => requestFactory.CreateRequestAsync(method, relativeUrl, payload, cancellationToken);

    private static async Task<KubernetesJobStatusSnapshot?> ParseJobResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return KubernetesJobManifestSerializer.ParseJobStatus(document.RootElement);
    }

    // DELETE responses can be either a Job body or a bare Status kind; the latter
    // parses cleanly but has no Job-specific fields, and some clusters send empty
    // or non-JSON bodies. Swallow parse failures so a malformed tail does not mask
    // a successful delete.
    private static async Task<KubernetesJobStatusSnapshot?> TryParseJobResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ParseJobResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
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
        // Intentional catch-all: this is a best-effort read of the error response body for
        // diagnostics; a failure to read it must not prevent the caller from surfacing the
        // original API error, so a placeholder is substituted instead.
        catch (Exception readEx) when (readEx is not OutOfMemoryException)
        {
            body = $"<unreadable response body: {readEx.Message}>";
        }

        Log.KubernetesApiError(logger, operation, (int)response.StatusCode, body);
        throw new HttpRequestException(
            $"Kubernetes API request to {operation} failed with status {(int)response.StatusCode}: {body}",
            inner: null,
            response.StatusCode);
    }

    internal static HttpMessageHandler CreatePrimaryHandler(bool inClusterAutoDetect, string? caBundlePath = null, ILogger? logger = null)
    {
        var handler = new HttpClientHandler();
        var resolvedPath = ResolveTrustedCaPath(inClusterAutoDetect, caBundlePath, InClusterCaCertPath);

        if (resolvedPath != null)
        {
            try
            {
                var trustedRoots = LoadCaBundle(resolvedPath);
                if (trustedRoots.Count > 0)
                {
                    handler.ServerCertificateCustomValidationCallback = (_, leaf, presentedChain, errors) =>
                        ValidateAgainstTrustedCas(leaf, presentedChain, errors, trustedRoots);
                }
            }
            // Intentional catch-all: best-effort CA bundle load at handler-creation time; a
            // malformed bundle must not prevent handler construction. Fall back to the
            // default OS trust store — the connection will still fail at the transport
            // layer if the CA is genuinely untrusted.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (logger is not null)
                {
                    Log.CaBundleLoadFailed(logger, ex, resolvedPath);
                }
            }
        }

        return handler;
    }

    // The projected in-cluster CA is used only when auto-detect is enabled; an explicit API server must trust the configured bundle.
    internal static string? ResolveTrustedCaPath(
        bool inClusterAutoDetect,
        string? configuredCaBundlePath,
        string inClusterCaCertPath)
    {
        if (inClusterAutoDetect
            && !string.IsNullOrEmpty(inClusterCaCertPath)
            && File.Exists(inClusterCaCertPath))
        {
            return inClusterCaCertPath;
        }

        if (!string.IsNullOrWhiteSpace(configuredCaBundlePath) && File.Exists(configuredCaBundlePath))
        {
            return configuredCaBundlePath;
        }

        return null;
    }

    private static X509Certificate2Collection LoadCaBundle(string pemPath)
    {
        var collection = new X509Certificate2Collection();
        collection.ImportFromPemFile(pemPath);
        return collection;
    }

    // A custom CA extends trust and still rejects a hostname mismatch or a missing certificate, so a thumbprint match cannot silence those failures.
    internal static bool ValidateAgainstTrustedCas(
        X509Certificate2? leaf,
        X509Chain? presentedChain,
        SslPolicyErrors errors,
        X509Certificate2Collection trustedRoots)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        // Only soften chain-trust errors; hostname mismatches and missing certificates
        // must continue to fail even though a custom CA is configured.
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        if (leaf == null)
        {
            return false;
        }

        using var customChain = new X509Chain();
        customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        customChain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots);
        // Kubernetes API server certificates typically lack an accessible CRL/OCSP
        // endpoint (private CA); skipping revocation keeps chain validation deterministic
        // while expiry/signature checks still apply via Build().
        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        // Forward any intermediate certificates the server presented alongside the leaf.
        // Without this, a valid root → intermediate → leaf chain fails because the
        // CustomTrustStore only holds the root and the intermediates are not otherwise
        // discoverable on the client.
        if (presentedChain != null)
        {
            foreach (var cert in presentedChain.ChainElements
                .Select(element => element.Certificate)
                .Where(cert => cert != null && !ReferenceEquals(cert, leaf)))
            {
                customChain.ChainPolicy.ExtraStore.Add(cert!);
            }
        }

        return customChain.Build(leaf);
    }

    internal static string? TryReadInClusterNamespace(ILogger? logger = null)
    {
        try
        {
            if (File.Exists(InClusterNamespacePath))
            {
                var ns = File.ReadAllText(InClusterNamespacePath).Trim();
                return string.IsNullOrEmpty(ns) ? null : ns;
            }
        }
        // Intentional catch-all: best-effort read of the kubelet-projected namespace file;
        // I/O failures are ignored and the adapter falls back to the configured default.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (logger is not null)
            {
                Log.InClusterNamespaceReadFailed(logger, ex);
            }
        }

        return null;
    }

    private static partial class Log
    {
        [LoggerMessage(9050, LogLevel.Warning,
            "Kubernetes API call to {Operation} failed with status {StatusCode}: {Body}")]
        public static partial void KubernetesApiError(ILogger logger, string operation, int statusCode, string body);

        [LoggerMessage(9051, LogLevel.Warning,
            "Failed to load the Kubernetes CA bundle from {CaBundlePath}; falling back to the default OS trust store.")]
        public static partial void CaBundleLoadFailed(ILogger logger, Exception exception, string caBundlePath);

        [LoggerMessage(9052, LogLevel.Debug,
            "Failed to read the projected in-cluster namespace file; falling back to the configured default.")]
        public static partial void InClusterNamespaceReadFailed(ILogger logger, Exception exception);
    }
}
