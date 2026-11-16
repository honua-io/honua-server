// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.CloudIntegration.Tests;

internal static class KubernetesBackendFactory
{
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

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
