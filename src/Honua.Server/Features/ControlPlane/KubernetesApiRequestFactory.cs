// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

internal sealed class KubernetesApiRequestFactory(IOptionsMonitor<KubernetesExecutionOptions> options)
{
    private const string InClusterTokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private const string InClusterHostEnv = "KUBERNETES_SERVICE_HOST";
    private const string InClusterPortEnv = "KUBERNETES_SERVICE_PORT";

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

    // The path is concatenated because a relative URL that starts with a slash would drop a gateway prefix on the base.
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
