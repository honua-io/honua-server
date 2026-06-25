// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Builds a production-shaped <see cref="KubernetesJobBatchComputeBackend"/> wired against a
/// real out-of-cluster API server (a kind cluster). The wiring mirrors
/// <c>Program.cs</c>: a named <c>control-plane-kubernetes</c> HttpClient whose primary
/// handler is <see cref="KubernetesJobClient.CreatePrimaryHandler"/> (so the kind cluster CA
/// is trusted exactly as it would be for a real private-CA target), the shared
/// <see cref="KubernetesApiRequestFactory"/>, and a <see cref="KubernetesExecutionOptions"/>
/// snapshot with in-cluster auto-detect disabled (out-of-cluster targeting).
/// </summary>
internal static class KubernetesBackendFactory
{
    /// <summary>
    /// Composes the backend and its real <see cref="IKubernetesJobClient"/> against the
    /// supplied kind cluster inputs. The returned <see cref="ServiceProvider"/> owns the
    /// HttpClient lifetime and must be disposed by the caller.
    /// </summary>
    public static (KubernetesJobBatchComputeBackend Backend, ServiceProvider Provider) Create(
        string apiServerUrl,
        string bearerToken,
        string caBundlePath,
        string @namespace,
        string? defaultImage)
    {
        var options = new KubernetesExecutionOptions
        {
            // Out-of-cluster: target the kind API server explicitly and trust its CA bundle,
            // exactly as an operator pointing Honua at a remote cluster would configure.
            InClusterAutoDetect = false,
            ApiServerUrl = apiServerUrl,
            BearerToken = bearerToken,
            CaBundlePath = caBundlePath,
            DefaultNamespace = @namespace,
            DefaultImage = defaultImage,
            // Clean completed Jobs quickly so repeated runs do not collide on Job name.
            DefaultTtlSecondsAfterFinished = 120
        };

        var optionsMonitor = new StaticOptionsMonitor<KubernetesExecutionOptions>(options);

        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<KubernetesExecutionOptions>>(optionsMonitor);
        services.AddHttpClient(KubernetesJobClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() =>
                KubernetesJobClient.CreatePrimaryHandler(
                    inClusterAutoDetect: false,
                    caBundlePath: caBundlePath));

        var provider = services.BuildServiceProvider();

        var requestFactory = new KubernetesApiRequestFactory(optionsMonitor);
        var jobClient = new KubernetesJobClient(
            provider.GetRequiredService<IHttpClientFactory>(),
            requestFactory,
            NullLogger<KubernetesJobClient>.Instance);

        var backend = new KubernetesJobBatchComputeBackend(
            jobClient,
            optionsMonitor,
            NullLogger<KubernetesJobBatchComputeBackend>.Instance);

        return (backend, provider);
    }

    /// <summary>
    /// Minimal <see cref="IOptionsMonitor{T}"/> over a fixed snapshot. The backend and request
    /// factory only read <see cref="IOptionsMonitor{T}.CurrentValue"/>; change notification is
    /// not exercised by the harness.
    /// </summary>
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
