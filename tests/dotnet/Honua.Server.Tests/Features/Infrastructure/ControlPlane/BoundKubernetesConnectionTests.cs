// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class BoundKubernetesConnectionTests
{
    [UnitTest]
    public async Task DifferentRegisteredEndpointOrCaCannotReceiveHostCredential()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, cert.ExportCertificatePem());
            var options = Substitute.For<IOptionsMonitor<KubernetesExecutionOptions>>();
            options.CurrentValue.Returns(new KubernetesExecutionOptions
            {
                InClusterAutoDetect = false, ApiServerUrl = "https://host-cluster.invalid", CaBundlePath = path, BearerToken = "never-sent",
            });
            var factory = new BoundKubernetesConnectionFactory(options);
            var target = new Dictionary<string, string>
            {
                ["kubernetes.api_server"] = "https://other-cluster.invalid",
                ["kubernetes.ca_sha256"] = Convert.ToHexStringLower(SHA256.HashData(cert.RawData)),
            };
            var wrongEndpoint = () => factory.OpenAsync(target, CancellationToken.None);
            await wrongEndpoint.Should().ThrowAsync<InvalidOperationException>().WithMessage("*endpoint*registered*");
            target["kubernetes.api_server"] = "https://host-cluster.invalid";
            target["kubernetes.ca_sha256"] = new string('a', 64);
            var wrongCa = () => factory.OpenAsync(target, CancellationToken.None);
            await wrongCa.Should().ThrowAsync<InvalidOperationException>().WithMessage("*CA*registered*");
        }
        finally { File.Delete(path); }
    }

    [UnitTest]
    public async Task DistinctConnectionsKeepEndpointsAndFixedCredentialLocatorsWhileTokensRotate()
    {
        var tokenA = Path.GetTempFileName();
        var tokenB = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tokenA, "token-a");
            await File.WriteAllTextAsync(tokenB, "token-b");
            var handler = new CaptureHandler();
            using var first = new BoundKubernetesConnection(new HttpClient(handler, false), new Uri("https://cluster-a.example/proxy"), tokenA, null);
            using var second = new BoundKubernetesConnection(new HttpClient(handler, false), new Uri("https://cluster-b.example"), tokenB, null);
            using var responseA = await first.SendAsync(HttpMethod.Get, "/api", null, CancellationToken.None);
            using var responseB = await second.SendAsync(HttpMethod.Get, "/api", null, CancellationToken.None);
            await File.WriteAllTextAsync(tokenA, "rotated-a");
            using var rotated = await first.SendAsync(HttpMethod.Get, "/api", null, CancellationToken.None);
            handler.Requests.Should().Equal(("https://cluster-a.example/proxy/api", "token-a"),
                ("https://cluster-b.example/api", "token-b"), ("https://cluster-a.example/proxy/api", "rotated-a"));
        }
        finally { File.Delete(tokenA); File.Delete(tokenB); }
    }

    [UnitTest]
    public async Task MissingCredentialFileAndMalformedHeaderNeverExposeCredentialOrLocator()
    {
        var path = Path.Combine(Path.GetTempPath(), "honua-private-credential-" + Guid.NewGuid().ToString("N"));
        var handler = new CaptureHandler();
        using var connection = new BoundKubernetesConnection(new HttpClient(handler, false), new Uri("https://cluster.example"), path, null);
        var missing = () => connection.SendAsync(HttpMethod.Get, "/api", null, CancellationToken.None);
        var failure = await missing.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.ToString().Should().NotContain(path);
        const string token = "private-header-value\r\nInjected: private-secret";
        try
        {
            await File.WriteAllTextAsync(path, token);
            var malformed = () => connection.SendAsync(HttpMethod.Get, "/api", null, CancellationToken.None);
            failure = await malformed.Should().ThrowAsync<InvalidOperationException>();
            failure.Which.ToString().Should().NotContain(path).And.NotContain("private-header-value").And.NotContain("private-secret");
            failure.Which.InnerException.Should().BeNull();
            handler.Requests.Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(string Uri, string? Token)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.Parameter));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
