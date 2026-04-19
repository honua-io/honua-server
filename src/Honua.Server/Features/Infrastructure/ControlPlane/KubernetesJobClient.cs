// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Result envelope returned by <see cref="IKubernetesJobClient.CreateJobAsync"/>.
/// Captures whether the Job was newly created (2xx), already existed (409 Conflict),
/// or was rejected, along with the current status snapshot when one is available.
/// </summary>
internal sealed record KubernetesJobCreateResult
{
    /// <summary>
    /// HTTP status code returned by the API.
    /// </summary>
    public required HttpStatusCode StatusCode { get; init; }

    /// <summary>
    /// True when the API server reported that a Job with the same name already exists.
    /// </summary>
    public bool AlreadyExists => StatusCode == HttpStatusCode.Conflict;

    /// <summary>
    /// Observed Job status immediately after the create call, or null when the server
    /// did not return a usable snapshot.
    /// </summary>
    public KubernetesJobStatusSnapshot? Snapshot { get; init; }
}

/// <summary>
/// Result envelope returned by <see cref="IKubernetesJobClient.GetJobAsync"/>.
/// </summary>
internal sealed record KubernetesJobFetchResult
{
    /// <summary>
    /// HTTP status code returned by the API.
    /// </summary>
    public required HttpStatusCode StatusCode { get; init; }

    /// <summary>
    /// True when the API server reported that the Job does not exist.
    /// </summary>
    public bool NotFound => StatusCode == HttpStatusCode.NotFound;

    /// <summary>
    /// Current Job status snapshot, or null when the Job is not present.
    /// </summary>
    public KubernetesJobStatusSnapshot? Snapshot { get; init; }
}

/// <summary>
/// Result envelope returned by <see cref="IKubernetesJobClient.DeleteJobAsync"/>.
/// </summary>
internal sealed record KubernetesJobDeleteResult
{
    /// <summary>
    /// HTTP status code returned by the API.
    /// </summary>
    public required HttpStatusCode StatusCode { get; init; }

    /// <summary>
    /// True when the API server reported that the Job was already gone.
    /// </summary>
    public bool NotFound => StatusCode == HttpStatusCode.NotFound;
}

/// <summary>
/// Narrow REST adapter over the Kubernetes <c>batch/v1</c> Job and <c>core/v1</c> Pod APIs.
/// Only the verbs needed by <see cref="KubernetesJobBatchComputeBackend"/> are modeled so
/// the surface stays AOT-friendly and free of external Kubernetes client dependencies.
/// </summary>
internal interface IKubernetesJobClient
{
    /// <summary>
    /// Creates a Kubernetes Job from the provided manifest. Returns a 409 Conflict
    /// result unchanged so the adapter can treat idempotent re-submissions as success.
    /// </summary>
    Task<KubernetesJobCreateResult> CreateJobAsync(
        KubernetesJobManifest manifest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the current status of a Job. Returns a 404 Not Found result unchanged.
    /// </summary>
    Task<KubernetesJobFetchResult> GetJobAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a Job with background cascade so associated pods are cleaned up.
    /// Returns a 404 Not Found result unchanged.
    /// </summary>
    Task<KubernetesJobDeleteResult> DeleteJobAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists pods matching a label selector inside a namespace. Used to surface
    /// container termination detail when a Job enters a terminal failure state.
    /// </summary>
    Task<IReadOnlyList<KubernetesPodStatusSnapshot>> ListPodsAsync(
        string @namespace,
        string labelSelector,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Raw-HTTP implementation of <see cref="IKubernetesJobClient"/>. Handles in-cluster
/// service-account auth via the projected token and CA bundle, and out-of-cluster
/// auth via an explicit bearer token file. Avoids taking a dependency on the
/// official <c>KubernetesClient</c> NuGet package to keep the trim/AOT surface small.
/// </summary>
internal sealed partial class KubernetesJobClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<KubernetesExecutionOptions> options,
    ILogger<KubernetesJobClient> logger) : IKubernetesJobClient
{
    internal const string HttpClientName = "control-plane-kubernetes";

    private const string InClusterTokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private const string InClusterCaCertPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
    private const string InClusterNamespacePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
    private const string InClusterHostEnv = "KUBERNETES_SERVICE_HOST";
    private const string InClusterPortEnv = "KUBERNETES_SERVICE_PORT";

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

        return new KubernetesJobDeleteResult { StatusCode = response.StatusCode };
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

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string relativeUrl,
        byte[]? payload,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveAuthentication();
        var absoluteUri = CombineApiServerUri(resolved.ApiServer, relativeUrl);
        var request = new HttpRequestMessage(method, absoluteUri);

        var token = await ReadTokenAsync(resolved, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (payload != null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = Encoding.UTF8.WebName
            };
        }

        return request;
    }

    /// <summary>
    /// Joins the API server base URL with a Kubernetes REST path while preserving any
    /// non-root path on the base (e.g. when the API server sits behind a path-based
    /// gateway at <c>https://proxy.example/k8s</c>). <see cref="Uri(Uri, string)"/> drops
    /// the base path whenever the relative URL starts with "/", so build the string
    /// explicitly instead.
    /// </summary>
    internal static Uri CombineApiServerUri(Uri apiServer, string relativeUrl)
    {
        ArgumentNullException.ThrowIfNull(apiServer);
        ArgumentException.ThrowIfNullOrEmpty(relativeUrl);

        var basePart = apiServer.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var suffix = relativeUrl.StartsWith('/') ? relativeUrl : "/" + relativeUrl;
        return new Uri(basePart + suffix, UriKind.Absolute);
    }

    private KubernetesAuthContext ResolveAuthentication()
    {
        var current = options.CurrentValue;

        if (current.InClusterAutoDetect && TryResolveInClusterContext(out var inCluster))
        {
            return inCluster;
        }

        if (string.IsNullOrWhiteSpace(current.ApiServerUrl))
        {
            throw new InvalidOperationException(
                "Kubernetes execution backend is configured but no API server endpoint is available. " +
                "Set ControlPlane:Kubernetes:ApiServerUrl or enable in-cluster auto-detection.");
        }

        return new KubernetesAuthContext(
            new Uri(current.ApiServerUrl, UriKind.Absolute),
            current.BearerTokenPath,
            current.BearerToken);
    }

    private static bool TryResolveInClusterContext(out KubernetesAuthContext context)
    {
        var host = Environment.GetEnvironmentVariable(InClusterHostEnv);
        var port = Environment.GetEnvironmentVariable(InClusterPortEnv);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port) || !File.Exists(InClusterTokenPath))
        {
            context = default!;
            return false;
        }

        context = new KubernetesAuthContext(
            new Uri($"https://{host}:{port}", UriKind.Absolute),
            InClusterTokenPath,
            BearerToken: null);
        return true;
    }

    private static async Task<string?> ReadTokenAsync(
        KubernetesAuthContext context,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(context.BearerToken))
        {
            return context.BearerToken;
        }

        if (string.IsNullOrEmpty(context.BearerTokenPath) || !File.Exists(context.BearerTokenPath))
        {
            return null;
        }

        var token = await File.ReadAllTextAsync(context.BearerTokenPath, cancellationToken).ConfigureAwait(false);
        return token.Trim();
    }

    private static async Task<KubernetesJobStatusSnapshot?> ParseJobResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return KubernetesJobManifestSerializer.ParseJobStatus(document.RootElement);
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

        Log.KubernetesApiError(logger, operation, (int)response.StatusCode, body);
        throw new HttpRequestException(
            $"Kubernetes API request to {operation} failed with status {(int)response.StatusCode}: {body}",
            inner: null,
            response.StatusCode);
    }

    /// <summary>
    /// Creates an <see cref="HttpMessageHandler"/> that trusts the Kubernetes API server CA.
    /// Prefers the in-cluster projected CA bundle when available; otherwise, trusts a
    /// PEM bundle at <paramref name="caBundlePath"/> if provided (for clusters fronted by
    /// a private or self-signed CA); otherwise falls back to the OS trust store so
    /// operators whose API server chains to a public CA keep working. Wired at DI
    /// registration time.
    /// </summary>
    internal static HttpMessageHandler CreatePrimaryHandler(string? caBundlePath = null)
    {
        var handler = new HttpClientHandler();
        var resolvedPath = File.Exists(InClusterCaCertPath)
            ? InClusterCaCertPath
            : (!string.IsNullOrWhiteSpace(caBundlePath) && File.Exists(caBundlePath) ? caBundlePath : null);

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
            catch (Exception)
            {
                // Fall back to the default OS trust store if the CA bundle is malformed;
                // the connection will still fail at the transport layer if the CA is untrusted.
            }
        }

        return handler;
    }

    private static X509Certificate2Collection LoadCaBundle(string pemPath)
    {
        var collection = new X509Certificate2Collection();
        collection.ImportFromPemFile(pemPath);
        return collection;
    }

    /// <summary>
    /// Validates the server certificate when a custom CA bundle is configured. Extends
    /// trust to the configured custom root(s) via <see cref="X509ChainTrustMode.CustomRootTrust"/>
    /// while preserving normal name-binding, expiry, and signature checks. Explicitly
    /// rejects hostname mismatches and missing-certificate errors so a thumbprint match
    /// alone cannot silence those policy failures. Intermediate certificates presented
    /// by the server are carried forward via <see cref="X509ChainPolicy.ExtraStore"/>
    /// so a root → intermediate → leaf chain verifies when the configured bundle only
    /// contains the root.
    /// </summary>
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
            foreach (var element in presentedChain.ChainElements)
            {
                var cert = element.Certificate;
                if (cert != null && !ReferenceEquals(cert, leaf))
                {
                    customChain.ChainPolicy.ExtraStore.Add(cert);
                }
            }
        }

        return customChain.Build(leaf);
    }

    /// <summary>
    /// Reads the projected namespace file written by the kubelet; used by the adapter
    /// to default the target namespace when none is configured explicitly.
    /// </summary>
    internal static string? TryReadInClusterNamespace()
    {
        try
        {
            if (File.Exists(InClusterNamespacePath))
            {
                var ns = File.ReadAllText(InClusterNamespacePath).Trim();
                return string.IsNullOrEmpty(ns) ? null : ns;
            }
        }
        catch
        {
            // Ignore I/O failures; the adapter falls back to the configured default.
        }

        return null;
    }

    private readonly record struct KubernetesAuthContext(
        Uri ApiServer,
        string? BearerTokenPath,
        string? BearerToken);

    private static partial class Log
    {
        [LoggerMessage(9050, LogLevel.Warning,
            "Kubernetes API call to {Operation} failed with status {StatusCode}: {Body}")]
        public static partial void KubernetesApiError(ILogger logger, string operation, int statusCode, string body);
    }
}
