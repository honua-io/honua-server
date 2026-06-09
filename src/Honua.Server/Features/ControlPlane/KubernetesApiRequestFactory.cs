// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>
/// Shared builder for authenticated Kubernetes API server requests. Resolves the
/// in-cluster service-account context (projected token + API server env vars) or the
/// explicitly configured out-of-cluster endpoint, attaches the bearer token, and
/// combines the API server base URL with a REST path. Extracted from
/// <see cref="KubernetesJobClient"/> so the Job batch backend and the Argo Rollouts
/// deploy backend share one credential-resolution and request-shaping path instead of
/// each duplicating the auth chain. Keeps the trim/AOT surface small by avoiding the
/// official <c>KubernetesClient</c> NuGet dependency.
/// </summary>
internal sealed class KubernetesApiRequestFactory(IOptionsMonitor<KubernetesExecutionOptions> options)
{
    private const string InClusterTokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private const string InClusterHostEnv = "KUBERNETES_SERVICE_HOST";
    private const string InClusterPortEnv = "KUBERNETES_SERVICE_PORT";

    /// <summary>
    /// Builds an authenticated <see cref="HttpRequestMessage"/> for the given Kubernetes
    /// REST path. When <paramref name="contentType"/> is supplied the payload is sent with
    /// that media type (used for JSON merge/strategic-merge PATCH bodies); otherwise it
    /// defaults to <c>application/json</c>.
    /// </summary>
    public async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string relativeUrl,
        byte[]? payload,
        CancellationToken cancellationToken,
        string contentType = "application/json")
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
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType)
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
                "Kubernetes control-plane backend is configured but no API server endpoint is available. " +
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
            context = default;
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

    private readonly record struct KubernetesAuthContext(
        Uri ApiServer,
        string? BearerTokenPath,
        string? BearerToken);
}
