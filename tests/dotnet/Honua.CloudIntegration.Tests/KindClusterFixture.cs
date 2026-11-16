// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.CloudIntegration.Tests;

public sealed class KindClusterFixture : IAsyncLifetime
{
    public const string ApiServerUrlEnvVar = "HONUA_TEST_K8S_API_SERVER_URL";

    public const string CaBundlePathEnvVar = "HONUA_TEST_K8S_CA_BUNDLE_PATH";

    public const string BearerTokenEnvVar = "HONUA_TEST_K8S_BEARER_TOKEN";

    public const string NamespaceEnvVar = "HONUA_TEST_K8S_NAMESPACE";

    public bool Available { get; private set; }

    public string? ApiServerUrl { get; private set; }

    public string? CaBundlePath { get; private set; }

    public string? BearerToken { get; private set; }

    public string Namespace { get; private set; } = "default";

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

    public Task DisposeAsync() => Task.CompletedTask;
}
