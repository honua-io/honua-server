// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class KubernetesJobClientTests
{
    [Fact]
    public void CombineApiServerUri_WithRootBase_PrefixesRelativePath()
    {
        var combined = KubernetesJobClient.CombineApiServerUri(
            new Uri("https://cluster.example"),
            "/apis/batch/v1/namespaces/honua/jobs");

        combined.AbsoluteUri.Should().Be("https://cluster.example/apis/batch/v1/namespaces/honua/jobs");
    }

    [Fact]
    public void CombineApiServerUri_WithTrailingSlashOnBase_ProducesSinglePathSeparator()
    {
        var combined = KubernetesJobClient.CombineApiServerUri(
            new Uri("https://cluster.example/"),
            "/apis/batch/v1/namespaces/honua/jobs");

        combined.AbsoluteUri.Should().Be("https://cluster.example/apis/batch/v1/namespaces/honua/jobs");
    }

    [Fact]
    public void CombineApiServerUri_WithPathPrefix_PreservesBasePath()
    {
        var combined = KubernetesJobClient.CombineApiServerUri(
            new Uri("https://proxy.example/k8s"),
            "/apis/batch/v1/namespaces/honua/jobs");

        combined.AbsoluteUri.Should().Be("https://proxy.example/k8s/apis/batch/v1/namespaces/honua/jobs");
    }

    [Fact]
    public void CombineApiServerUri_WithTrailingSlashOnPathPrefix_ProducesSingleSeparator()
    {
        var combined = KubernetesJobClient.CombineApiServerUri(
            new Uri("https://proxy.example/k8s/"),
            "/apis/batch/v1/namespaces/honua/jobs");

        combined.AbsoluteUri.Should().Be("https://proxy.example/k8s/apis/batch/v1/namespaces/honua/jobs");
    }

    [Fact]
    public void CombineApiServerUri_DropsBaseQueryAndFragment()
    {
        var combined = KubernetesJobClient.CombineApiServerUri(
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
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(leafCert, SslPolicyErrors.None, roots);
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
            var result = KubernetesJobClient.ValidateAgainstTrustedCas(leafCert, combined, roots);
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
}
