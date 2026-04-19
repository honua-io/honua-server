// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ControlPlaneOptionsValidatorTests
{
    private readonly ControlPlaneOptionsValidator _validator = new();

    [UnitTest]
    public void Validate_WithPublicHttpsTelemetryConnection_ReturnsSuccess()
    {
        var options = new ControlPlaneOptions
        {
            TelemetryConnections =
            [
                new DeployTelemetryConnectionOptions
                {
                    ConnectionId = "prod-prom",
                    Provider = "prometheus",
                    BaseUrl = "https://example.com",
                    QueryPath = "/api/v1/query",
                    AuthHeaderName = "Authorization",
                    AuthHeaderValue = "Bearer abc123",
                    TimeoutSeconds = 10
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    [UnitTest]
    public void Validate_WithPrivateTelemetryBaseUrl_ReturnsFailure()
    {
        var options = new ControlPlaneOptions
        {
            TelemetryConnections =
            [
                new DeployTelemetryConnectionOptions
                {
                    ConnectionId = "prod-prom",
                    Provider = "prometheus",
                    BaseUrl = "https://127.0.0.1:9090",
                    TimeoutSeconds = 10
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("BaseUrl") &&
            failure.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void Validate_WithDisallowedTelemetryAuthHeader_ReturnsFailure()
    {
        var options = new ControlPlaneOptions
        {
            TelemetryConnections =
            [
                new DeployTelemetryConnectionOptions
                {
                    ConnectionId = "prod-prom",
                    Provider = "prometheus",
                    BaseUrl = "https://example.com",
                    QueryPath = "/api/v1/query",
                    AuthHeaderName = "Host",
                    AuthHeaderValue = "internal.example",
                    TimeoutSeconds = 10
                }
            ]
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("AuthHeaderName") &&
            failure.Contains("not allowed", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void Validate_WithMalformedKubernetesApiServerUrl_ReturnsFailure()
    {
        var options = new ControlPlaneOptions
        {
            Kubernetes = new KubernetesExecutionOptions
            {
                InClusterAutoDetect = false,
                ApiServerUrl = "not-a-url"
            }
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("ApiServerUrl") &&
            failure.Contains("absolute URL", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void Validate_WithAutoDetectDisabledAndNoApiServerUrl_ReturnsFailure()
    {
        var options = new ControlPlaneOptions
        {
            Kubernetes = new KubernetesExecutionOptions
            {
                InClusterAutoDetect = false,
                ApiServerUrl = null
            }
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("ApiServerUrl") &&
            failure.Contains("InClusterAutoDetect", StringComparison.Ordinal));
    }

    [UnitTest]
    public void Validate_WithMissingKubernetesCaBundlePath_ReturnsFailure()
    {
        var options = new ControlPlaneOptions
        {
            Kubernetes = new KubernetesExecutionOptions
            {
                InClusterAutoDetect = false,
                ApiServerUrl = "https://cluster.example.test",
                CaBundlePath = "/this/path/does/not/exist/ca.pem"
            }
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("CaBundlePath"));
    }

    [UnitTest]
    public void Validate_WithInClusterAutoDetect_AndNoApiServerUrl_Succeeds()
    {
        var options = new ControlPlaneOptions
        {
            Kubernetes = new KubernetesExecutionOptions
            {
                InClusterAutoDetect = true,
                ApiServerUrl = null
            }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }
}
