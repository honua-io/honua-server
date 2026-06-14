// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Honua.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class KubernetesJobClientTests
{
    [Fact]
    public void CombineApiServerUri_WithRootBase_PrefixesRelativePath()
    {
        var combined = KubernetesApiRequestFactory.CombineApiServerUri(
            new Uri("https://cluster.example"),
            "/apis/batch/v1/namespaces/honua/jobs");

        combined.AbsoluteUri.Should().Be("https://cluster.example/apis/batch/v1/namespaces/honua/jobs");
    }

    [Fact]
    public void CombineApiServerUri_WithTrailingSlashOnBase_ProducesSinglePathSeparator()
    {
        var combined = KubernetesApiRequestFactory.CombineApiServerUri(
            new Uri("https://cluster.example/"),
            "/apis/batch/v1/namespaces/honua/jobs");

        combined.AbsoluteUri.Should().Be("https://cluster.example/apis/batch/v1/namespaces/honua/jobs");
    }

    [Fact]
    public void CombineApiServerUri_WithPathPrefix_PreservesBasePath()
    {
        var combined = KubernetesApiRequestFactory.CombineApiServerUri(
            new Uri("https://proxy.example/k8s"),
            "/apis/batch/v1/namespaces/honua/jobs");

        combined.AbsoluteUri.Should().Be("https://proxy.example/k8s/apis/batch/v1/namespaces/honua/jobs");
    }

    [Fact]
    public void CombineApiServerUri_WithTrailingSlashOnPathPrefix_ProducesSingleSeparator()
    {
        var combined = KubernetesApiRequestFactory.CombineApiServerUri(
            new Uri("https://proxy.example/k8s/"),
            "/apis/batch/v1/namespaces/honua/jobs");

        combined.AbsoluteUri.Should().Be("https://proxy.example/k8s/apis/batch/v1/namespaces/honua/jobs");
    }

    [Fact]
    public void CombineApiServerUri_DropsBaseQueryAndFragment()
    {
        var combined = KubernetesApiRequestFactory.CombineApiServerUri(
            new Uri("https://proxy.example/k8s?leak=yes#frag"),
            "/apis/batch/v1/namespaces/honua/jobs");

        combined.AbsoluteUri.Should().Be("https://proxy.example/k8s/apis/batch/v1/namespaces/honua/jobs");
    }

    [Fact]
    public void ValidateAgainstTrustedCas_WithNoSslErrors_ReturnsTrueWithoutRebuildingChain()
    {
        var (rootCert, leafCert) = CreateTestChain("CN=TestRoot", "CN=leaf.example");
        try
        {
            var roots = new X509Certificate2Collection { rootCert };
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(
                leafCert, presentedChain: null, SslPolicyErrors.None, roots);
            result.Should().BeTrue();
        }
        finally
        {
            rootCert.Dispose();
            leafCert.Dispose();
        }
    }

    [Fact]
    public void ValidateAgainstTrustedCas_ChainsToCustomRoot_ReturnsTrue()
    {
        var (rootCert, leafCert) = CreateTestChain("CN=TestRoot", "CN=leaf.example");
        try
        {
            var roots = new X509Certificate2Collection { rootCert };
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(
                leafCert,
                presentedChain: null,
                SslPolicyErrors.RemoteCertificateChainErrors,
                roots);
            result.Should().BeTrue();
        }
        finally
        {
            rootCert.Dispose();
            leafCert.Dispose();
        }
    }

    [Fact]
    public void ValidateAgainstTrustedCas_RejectsHostnameMismatch()
    {
        var (rootCert, leafCert) = CreateTestChain("CN=TestRoot", "CN=leaf.example");
        try
        {
            var roots = new X509Certificate2Collection { rootCert };
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(
                leafCert,
                presentedChain: null,
                SslPolicyErrors.RemoteCertificateNameMismatch,
                roots);
            result.Should().BeFalse();
        }
        finally
        {
            rootCert.Dispose();
            leafCert.Dispose();
        }
    }

    [Fact]
    public void ValidateAgainstTrustedCas_RejectsHostnameMismatchEvenWhenChainTrusted()
    {
        var (rootCert, leafCert) = CreateTestChain("CN=TestRoot", "CN=leaf.example");
        try
        {
            var roots = new X509Certificate2Collection { rootCert };
            var combined = SslPolicyErrors.RemoteCertificateNameMismatch
                | SslPolicyErrors.RemoteCertificateChainErrors;
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(
                leafCert, presentedChain: null, combined, roots);
            result.Should().BeFalse();
        }
        finally
        {
            rootCert.Dispose();
            leafCert.Dispose();
        }
    }

    [Fact]
    public void ValidateAgainstTrustedCas_RejectsMissingCertificate()
    {
        var (rootCert, leafCert) = CreateTestChain("CN=TestRoot", "CN=leaf.example");
        try
        {
            var roots = new X509Certificate2Collection { rootCert };
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(
                leafCert,
                presentedChain: null,
                SslPolicyErrors.RemoteCertificateNotAvailable,
                roots);
            result.Should().BeFalse();
        }
        finally
        {
            rootCert.Dispose();
            leafCert.Dispose();
        }
    }

    [Fact]
    public void ValidateAgainstTrustedCas_RejectsExpiredLeafCertificate()
    {
        var (rootCert, expiredLeaf) = CreateTestChain(
            "CN=TestRoot",
            "CN=leaf.example",
            leafNotBefore: DateTimeOffset.UtcNow.AddDays(-10),
            leafNotAfter: DateTimeOffset.UtcNow.AddDays(-1));
        try
        {
            var roots = new X509Certificate2Collection { rootCert };
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(
                expiredLeaf,
                presentedChain: null,
                SslPolicyErrors.RemoteCertificateChainErrors,
                roots);
            result.Should().BeFalse();
        }
        finally
        {
            rootCert.Dispose();
            expiredLeaf.Dispose();
        }
    }

    [Fact]
    public void ValidateAgainstTrustedCas_RejectsLeafNotChainingToTrustedRoot()
    {
        var (trustedRoot, _) = CreateTestChain("CN=TrustedRoot", "CN=trusted.example");
        var (_, untrustedLeaf) = CreateTestChain("CN=OtherRoot", "CN=other.example");
        try
        {
            var roots = new X509Certificate2Collection { trustedRoot };
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(
                untrustedLeaf,
                presentedChain: null,
                SslPolicyErrors.RemoteCertificateChainErrors,
                roots);
            result.Should().BeFalse();
        }
        finally
        {
            trustedRoot.Dispose();
            untrustedLeaf.Dispose();
        }
    }

    [Fact]
    public void ValidateAgainstTrustedCas_WithIntermediate_CarriesPresentedIntermediatesForward()
    {
        // Private-PKI clusters commonly present a root→intermediate→leaf chain. The
        // custom trust store only carries the root, so the intermediate has to come
        // from the chain argument or Build() fails with PartialChain.
        var (rootCert, intermediateCert, leafCert) = CreateIntermediateChain(
            rootSubject: "CN=TestRoot",
            intermediateSubject: "CN=TestIntermediate",
            leafSubject: "CN=leaf.example");

        using var presentedChain = new X509Chain();
        presentedChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        presentedChain.ChainPolicy.CustomTrustStore.Add(rootCert);
        presentedChain.ChainPolicy.ExtraStore.Add(intermediateCert);
        presentedChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        presentedChain.Build(leafCert);

        try
        {
            // Trust bundle contains only the root — the intermediate must arrive via
            // the presented chain argument.
            var roots = new X509Certificate2Collection { rootCert };
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(
                leafCert,
                presentedChain,
                SslPolicyErrors.RemoteCertificateChainErrors,
                roots);

            result.Should().BeTrue("the presented intermediate must be carried into ExtraStore");
        }
        finally
        {
            rootCert.Dispose();
            intermediateCert.Dispose();
            leafCert.Dispose();
        }
    }

    [Fact]
    public void ResolveTrustedCaPath_InClusterAutoDetectFalse_IgnoresProjectedInClusterCa()
    {
        // Simulates a Honua host running inside one Kubernetes cluster (so the projected
        // service-account CA file exists) but targeting a *different* private-CA cluster
        // via explicit ApiServerUrl + CaBundlePath. InClusterAutoDetect=false must cause
        // the configured bundle to be honored; otherwise the adapter silently validates
        // the remote cluster's cert against the local cluster's CA and every call fails.
        var projectedCa = Path.GetTempFileName();
        var configuredCa = Path.GetTempFileName();
        try
        {
            File.WriteAllText(projectedCa, "in-cluster-pem");
            File.WriteAllText(configuredCa, "configured-pem");

            var resolved = KubernetesJobClient.ResolveTrustedCaPath(
                inClusterAutoDetect: false,
                configuredCaBundlePath: configuredCa,
                inClusterCaCertPath: projectedCa);

            resolved.Should().Be(
                configuredCa,
                "InClusterAutoDetect=false must not hijack TLS validation with the local projected CA");
        }
        finally
        {
            File.Delete(projectedCa);
            File.Delete(configuredCa);
        }
    }

    [Fact]
    public void ResolveTrustedCaPath_InClusterAutoDetectTrue_PrefersProjectedInClusterCa()
    {
        var projectedCa = Path.GetTempFileName();
        var configuredCa = Path.GetTempFileName();
        try
        {
            File.WriteAllText(projectedCa, "in-cluster-pem");
            File.WriteAllText(configuredCa, "configured-pem");

            var resolved = KubernetesJobClient.ResolveTrustedCaPath(
                inClusterAutoDetect: true,
                configuredCaBundlePath: configuredCa,
                inClusterCaCertPath: projectedCa);

            resolved.Should().Be(projectedCa);
        }
        finally
        {
            File.Delete(projectedCa);
            File.Delete(configuredCa);
        }
    }

    [Fact]
    public void ResolveTrustedCaPath_InClusterAutoDetectTrueButProjectedCaMissing_FallsBackToConfiguredBundle()
    {
        var configuredCa = Path.GetTempFileName();
        try
        {
            File.WriteAllText(configuredCa, "configured-pem");

            var resolved = KubernetesJobClient.ResolveTrustedCaPath(
                inClusterAutoDetect: true,
                configuredCaBundlePath: configuredCa,
                inClusterCaCertPath: Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"));

            resolved.Should().Be(configuredCa);
        }
        finally
        {
            File.Delete(configuredCa);
        }
    }

    [Fact]
    public void ResolveTrustedCaPath_NoBundlesAvailable_ReturnsNullForOsTrustStore()
    {
        var resolved = KubernetesJobClient.ResolveTrustedCaPath(
            inClusterAutoDetect: false,
            configuredCaBundlePath: null,
            inClusterCaCertPath: Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"));

        resolved.Should().BeNull();
    }

    [Fact]
    public void ValidateAgainstTrustedCas_WithIntermediate_FailsWhenIntermediateUnavailable()
    {
        var (rootCert, intermediateCert, leafCert) = CreateIntermediateChain(
            rootSubject: "CN=TestRoot",
            intermediateSubject: "CN=TestIntermediate",
            leafSubject: "CN=leaf.example");

        try
        {
            var roots = new X509Certificate2Collection { rootCert };

            // No presentedChain → the intermediate isn't discoverable, so the build
            // must fail rather than silently trusting the leaf.
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(
                leafCert,
                presentedChain: null,
                SslPolicyErrors.RemoteCertificateChainErrors,
                roots);

            result.Should().BeFalse();
        }
        finally
        {
            rootCert.Dispose();
            intermediateCert.Dispose();
            leafCert.Dispose();
        }
    }

    private static (X509Certificate2 Root, X509Certificate2 Leaf) CreateTestChain(
        string rootSubject,
        string leafSubject,
        DateTimeOffset? leafNotBefore = null,
        DateTimeOffset? leafNotAfter = null)
    {
        using var rootRsa = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            rootSubject,
            rootRsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        var rootCert = rootRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddYears(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        using var leafRsa = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            leafSubject,
            leafRsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        var serial = new byte[8];
        Random.Shared.NextBytes(serial);
        var leafCert = leafRequest.Create(
            rootCert,
            leafNotBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            leafNotAfter ?? DateTimeOffset.UtcNow.AddYears(1),
            serial);

        // The cert produced by .Create() does not carry the private key; we only need
        // validation and chain-building, not signing, so the public cert is sufficient.
        return (rootCert, leafCert);
    }

    private static (X509Certificate2 Root, X509Certificate2 Intermediate, X509Certificate2 Leaf) CreateIntermediateChain(
        string rootSubject,
        string intermediateSubject,
        string leafSubject)
    {
        using var rootRsa = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            rootSubject,
            rootRsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 1, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        var rootCert = rootRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddYears(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        using var intermediateRsa = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest(
            intermediateSubject,
            intermediateRsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        var intermediateSerial = new byte[8];
        Random.Shared.NextBytes(intermediateSerial);
        var intermediatePublic = intermediateRequest.Create(
            rootCert,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(3),
            intermediateSerial);
        var intermediateCert = intermediatePublic.CopyWithPrivateKey(intermediateRsa);
        intermediatePublic.Dispose();

        using var leafRsa = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            leafSubject,
            leafRsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        var leafSerial = new byte[8];
        Random.Shared.NextBytes(leafSerial);
        var leafCert = leafRequest.Create(
            intermediateCert,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            leafSerial);

        return (rootCert, intermediateCert, leafCert);
    }
}
