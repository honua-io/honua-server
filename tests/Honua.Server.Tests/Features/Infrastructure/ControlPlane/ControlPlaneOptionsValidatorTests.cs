// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    public void Validate_WithNonHttpsKubernetesApiServerUrl_ReturnsFailure()
    {
        var options = new ControlPlaneOptions
        {
            Kubernetes = new KubernetesExecutionOptions
            {
                InClusterAutoDetect = false,
                ApiServerUrl = "http://cluster.example.test"
            }
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("ApiServerUrl") &&
            failure.Contains("https scheme", StringComparison.Ordinal));
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

    [UnitTest]
    public void Validate_WithMissingKubernetesBearerTokenPath_ReturnsFailure()
    {
        var options = new ControlPlaneOptions
        {
            Kubernetes = new KubernetesExecutionOptions
            {
                InClusterAutoDetect = false,
                ApiServerUrl = "https://cluster.example.test",
                BearerTokenPath = "/this/path/does/not/exist/token"
            }
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("BearerTokenPath"));
    }

    [UnitTest]
    public void Validate_WithExistingKubernetesBearerTokenPath_Succeeds()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"bearer-token-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempFile, "test-token");
        try
        {
            var options = new ControlPlaneOptions
            {
                Kubernetes = new KubernetesExecutionOptions
                {
                    InClusterAutoDetect = false,
                    ApiServerUrl = "https://cluster.example.test",
                    BearerTokenPath = tempFile
                }
            };

            var result = _validator.Validate(null, options);

            Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [UnitTest]
    public void Validate_WithEmptyKubernetesCaBundleFile_ReturnsFailure()
    {
        // An empty CA bundle silently falls back to OS trust at runtime
        // (KubernetesJobClient swallows ImportFromPemFile failures) which fails only
        // later as private-CA TLS errors. Options validation must refuse to start.
        var tempFile = Path.Combine(Path.GetTempPath(), $"ca-bundle-empty-{Guid.NewGuid():N}.pem");
        File.WriteAllText(tempFile, string.Empty);
        try
        {
            var options = new ControlPlaneOptions
            {
                Kubernetes = new KubernetesExecutionOptions
                {
                    InClusterAutoDetect = false,
                    ApiServerUrl = "https://cluster.example.test",
                    CaBundlePath = tempFile
                }
            };

            var result = _validator.Validate(null, options);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Failures ?? Array.Empty<string>(), failure =>
                failure.Contains("CaBundlePath") &&
                failure.Contains("PEM-encoded certificates", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [UnitTest]
    public void Validate_WithMalformedKubernetesCaBundleFile_ReturnsFailure()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ca-bundle-bad-{Guid.NewGuid():N}.pem");
        File.WriteAllText(
            tempFile,
            "-----BEGIN CERTIFICATE-----\nnot-a-real-base64-encoded-cert\n-----END CERTIFICATE-----\n");
        try
        {
            var options = new ControlPlaneOptions
            {
                Kubernetes = new KubernetesExecutionOptions
                {
                    InClusterAutoDetect = false,
                    ApiServerUrl = "https://cluster.example.test",
                    CaBundlePath = tempFile
                }
            };

            var result = _validator.Validate(null, options);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Failures ?? Array.Empty<string>(), failure =>
                failure.Contains("CaBundlePath") &&
                (failure.Contains("PEM certificate bundle", StringComparison.Ordinal) ||
                 failure.Contains("PEM-encoded certificates", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [UnitTest]
    public void Validate_WithValidKubernetesCaBundleFile_Succeeds()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ca-bundle-ok-{Guid.NewGuid():N}.pem");
        File.WriteAllText(tempFile, CreateSelfSignedCaPem());
        try
        {
            var options = new ControlPlaneOptions
            {
                Kubernetes = new KubernetesExecutionOptions
                {
                    InClusterAutoDetect = false,
                    ApiServerUrl = "https://cluster.example.test",
                    CaBundlePath = tempFile
                }
            };

            var result = _validator.Validate(null, options);

            Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static string CreateSelfSignedCaPem()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=honua-test-ca",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
        return cert.ExportCertificatePem() + Environment.NewLine;
    }
}
