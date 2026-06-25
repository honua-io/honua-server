// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Fixture that adapts an already-provisioned <c>kind</c> (Kubernetes-in-Docker) cluster
/// into the inputs <c>KubernetesJobBatchComputeBackend</c> needs out-of-cluster: an HTTPS
/// API server URL, a PEM CA bundle path, a bearer token, and a target namespace.
///
/// Why env-var hand-off rather than parsing kubeconfig here: kind is provisioned by the CI
/// workflow's <c>kind</c> action (or a local <c>kind create cluster</c>), which already owns
/// the cluster lifecycle. The workflow extracts the server URL, cluster CA, and a
/// ServiceAccount token with <c>kubectl</c> — the canonical Kubernetes tool — and exports
/// them as the environment variables below. This keeps the harness free of a YAML/kubeconfig
/// parser and a Kubernetes client dependency (the production
/// <c>KubernetesJobClient</c> is itself a raw-HTTP adapter that avoids the official client),
/// and means the harness drives the SAME credential surface
/// (<c>ControlPlane:Kubernetes:ApiServerUrl</c> / <c>BearerToken</c> / <c>CaBundlePath</c>)
/// an operator configures for a real out-of-cluster target.
///
/// When the required variables are absent the fixture reports <see cref="Available"/> = false
/// and the kind-backed tests skip, so the project still compiles and runs (skipping) where no
/// cluster is present (for example a developer box without kind).
/// </summary>
public sealed class KindClusterFixture : IAsyncLifetime
{
    /// <summary>HTTPS API server URL of the kind cluster (must be https per option validation).</summary>
    public const string ApiServerUrlEnvVar = "HONUA_TEST_K8S_API_SERVER_URL";

    /// <summary>Filesystem path to a PEM CA bundle that signs the kind API server certificate.</summary>
    public const string CaBundlePathEnvVar = "HONUA_TEST_K8S_CA_BUNDLE_PATH";

    /// <summary>Bearer token for a ServiceAccount permitted to create/observe/delete Jobs.</summary>
    public const string BearerTokenEnvVar = "HONUA_TEST_K8S_BEARER_TOKEN";

    /// <summary>Namespace the test Jobs are created in (defaults to <c>default</c>).</summary>
    public const string NamespaceEnvVar = "HONUA_TEST_K8S_NAMESPACE";

    /// <summary>
    /// True when all required cluster inputs are present so the kind-backed scenarios can run.
    /// </summary>
    public bool Available { get; private set; }

    /// <summary>HTTPS API server URL, or null when unavailable.</summary>
    public string? ApiServerUrl { get; private set; }

    /// <summary>CA bundle path, or null when unavailable.</summary>
    public string? CaBundlePath { get; private set; }

    /// <summary>Bearer token, or null when unavailable.</summary>
    public string? BearerToken { get; private set; }

    /// <summary>Target namespace; defaults to <c>default</c>.</summary>
    public string Namespace { get; private set; } = "default";

    /// <summary>
    /// Resolves the cluster inputs from the environment. Synchronous in practice but kept
    /// async to satisfy <see cref="IAsyncLifetime"/>.
    /// </summary>
    public Task InitializeAsync()
    {
        ApiServerUrl = Environment.GetEnvironmentVariable(ApiServerUrlEnvVar);
        CaBundlePath = Environment.GetEnvironmentVariable(CaBundlePathEnvVar);
        BearerToken = Environment.GetEnvironmentVariable(BearerTokenEnvVar);
        var configuredNamespace = Environment.GetEnvironmentVariable(NamespaceEnvVar);
        if (!string.IsNullOrWhiteSpace(configuredNamespace))
        {
            Namespace = configuredNamespace.Trim();
        }

        Available =
            !string.IsNullOrWhiteSpace(ApiServerUrl) &&
            !string.IsNullOrWhiteSpace(BearerToken) &&
            !string.IsNullOrWhiteSpace(CaBundlePath) &&
            File.Exists(CaBundlePath);

        return Task.CompletedTask;
    }

    /// <summary>No-op; the kind cluster lifecycle is owned by the workflow that created it.</summary>
    public Task DisposeAsync() => Task.CompletedTask;
}
