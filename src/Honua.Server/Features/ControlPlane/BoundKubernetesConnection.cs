// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>Host-only connection selection pinned for the entire deployment API invocation.</summary>
internal interface IBoundKubernetesConnectionFactory
{
    Task<BoundKubernetesConnection> OpenAsync(IReadOnlyDictionary<string, string> target, CancellationToken cancellationToken);
}

internal sealed class BoundKubernetesConnectionFactory(IOptionsMonitor<KubernetesExecutionOptions> options)
    : IBoundKubernetesConnectionFactory
{
    public async Task<BoundKubernetesConnection> OpenAsync(IReadOnlyDictionary<string, string> target, CancellationToken cancellationToken)
    {
        try { return await OpenCoreAsync(target, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        { throw new InvalidOperationException("The host Kubernetes connection configuration is unavailable or invalid."); }
    }

    private async Task<BoundKubernetesConnection> OpenCoreAsync(IReadOnlyDictionary<string, string> target, CancellationToken cancellationToken)
    {
        // Snapshot once. Neither credentials nor destinations are accepted from a deploy request.
        var current = options.CurrentValue;
        var inCluster = current.InClusterAutoDetect && File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/token") &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));
        var endpoint = inCluster
            ? $"https://{Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")}:{Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443"}"
            : current.ApiServerUrl;
        var caPath = inCluster ? "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt" : current.CaBundlePath;
        var tokenPath = inCluster ? "/var/run/secrets/kubernetes.io/serviceaccount/token" : current.BearerTokenPath;
        var literalToken = inCluster ? null : current.BearerToken;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrWhiteSpace(caPath) || !File.Exists(caPath))
            throw new InvalidOperationException("A pinned HTTPS Kubernetes connection and CA are required.");
        var expectedEndpoint = Required(target, "kubernetes.api_server");
        if (!inCluster && uri.AbsoluteUri.TrimEnd('/') != new Uri(expectedEndpoint, UriKind.Absolute).AbsoluteUri.TrimEnd('/'))
            throw new InvalidOperationException("The host Kubernetes endpoint does not match the registered cluster.");
        var pem = await File.ReadAllTextAsync(caPath, cancellationToken).ConfigureAwait(false);
        var roots = new X509Certificate2Collection();
        roots.ImportFromPem(pem);
        if (roots.Count != 1 || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(roots[0].RawData)),
                Required(target, "kubernetes.ca_sha256"), StringComparison.Ordinal))
        {
            foreach (var root in roots) root.Dispose();
            throw new InvalidOperationException("The host Kubernetes CA does not match the registered cluster.");
        }
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (_, leaf, _, errors) =>
        {
            if (leaf is null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(roots[0]);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(leaf);
        };
        var connection = new BoundKubernetesConnection(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 4 * 1024 * 1024 }, uri,
            tokenPath, literalToken, roots[0]);
        try
        {
            using var response = await connection.SendAsync(HttpMethod.Get, "/api/v1/namespaces/kube-system", null, cancellationToken).ConfigureAwait(false);
            using var cluster = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (cluster.RootElement.GetProperty("metadata").GetProperty("uid").GetString() != Required(target, "kubernetes.cluster_uid"))
                throw new InvalidOperationException("The Kubernetes API does not represent the registered cluster.");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static string Required(IReadOnlyDictionary<string, string> target, string key)
        => target.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value
            : throw new InvalidOperationException("The registered Kubernetes target is incomplete.");
}

/// <summary>Matched immutable endpoint, trust anchor and host credential locator; never shared ambient state.</summary>
internal sealed class BoundKubernetesConnection(HttpClient client, Uri endpoint, string? tokenPath, string? literalToken,
    X509Certificate2? root = null) : IDisposable
{
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[]? patch, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, KubernetesApiRequestFactory.CombineApiServerUri(endpoint, path));
        try
        {
            var token = literalToken ?? (string.IsNullOrWhiteSpace(tokenPath) ? null :
                (await File.ReadAllTextAsync(tokenPath, cancellationToken).ConfigureAwait(false)).Trim());
            if (string.IsNullOrWhiteSpace(token) || token.Length > 32768 || token.Any(c =>
                    !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '~' or '+' or '/' or '=')))
                throw new InvalidOperationException("The host Kubernetes credential is unavailable or invalid.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        { throw new InvalidOperationException("The host Kubernetes credential is unavailable or invalid."); }
        if (patch is not null)
        {
            request.Content = new ByteArrayContent(patch);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json-patch+json");
        }
        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException("The bound Kubernetes API request failed.");
        }
        return response;
    }

    public void Dispose() { client.Dispose(); root?.Dispose(); }
}
