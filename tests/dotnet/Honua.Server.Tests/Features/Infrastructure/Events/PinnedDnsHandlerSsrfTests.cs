// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using ImportHttpClientHelper = Honua.Import.FileImport.ImportHttpClientHelper;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

/// <summary>
/// Connect-time SSRF coverage for the shared pinned-DNS outbound handler
/// (threat-model residual #1576). The handler backs webhook delivery and the
/// import-from-URL surfaces; these tests prove that requests targeting
/// private, loopback, link-local, and cloud-metadata addresses are rejected
/// before any socket connection is attempted — for both literal IP targets
/// and host names whose DNS resolution lands in a blocked range
/// (DNS-rebinding defense).
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class PinnedDnsHandlerSsrfTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // AWS/cloud metadata endpoint
    [InlineData("http://10.0.0.5/internal")] // RFC1918 private
    [InlineData("http://172.16.0.1/")] // RFC1918 private
    [InlineData("http://192.168.1.10/router")] // RFC1918 private
    [InlineData("http://127.0.0.1/admin")] // loopback
    [InlineData("http://100.64.0.1/")] // CGNAT (RFC 6598)
    [InlineData("http://[fe80::1]/")] // IPv6 link-local
    [InlineData("http://[::1]/")] // IPv6 loopback
    public async Task PinnedDnsHandler_LiteralBlockedAddress_RejectsBeforeConnect(string url)
    {
        using var client = CreateClient(WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler(
            hostAddressResolver: (_, _) => throw new InvalidOperationException(
                "Literal IP targets must not trigger DNS resolution.")));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));

        AssertBlockedAddress(exception);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    [Fact]
    public async Task PinnedDnsHandler_HostResolvingToCloudMetadataAddress_RejectsBeforeConnect()
    {
        // DNS-rebinding shape: a benign-looking host name resolves to the cloud
        // metadata endpoint. The pinned resolver result must be validated.
        using var client = CreateClient(WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler(
            hostAddressResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") })));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://metadata.attacker.example/latest/meta-data/"));

        AssertBlockedAddress(exception);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    [Fact]
    public async Task PinnedDnsHandler_HostResolvingToMixedPublicAndPrivateAddresses_RejectsBeforeConnect()
    {
        // A single private address in the answer set poisons the whole
        // resolution: the handler must not fall back to the public address.
        using var client = CreateClient(WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler(
            hostAddressResolver: (_, _) => Task.FromResult(new[]
            {
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("10.0.0.5")
            })));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://rebind.attacker.example/payload"));

        AssertBlockedAddress(exception);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    [Fact]
    public async Task PinnedDnsHandler_HostWithEmptyResolution_RejectsBeforeConnect()
    {
        using var client = CreateClient(WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler(
            hostAddressResolver: (_, _) => Task.FromResult(Array.Empty<IPAddress>())));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://unresolvable.example/"));

        AssertBlockedAddress(exception);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    [Fact]
    public async Task ImportHttpClientHandler_LiteralCloudMetadataAddress_RejectsBeforeConnect()
    {
        // The import-from-URL surface delegates to the same pinned-DNS handler;
        // prove the wiring blocks the metadata endpoint end to end.
        using var client = CreateClient(ImportHttpClientHelper.CreatePinnedDnsHttpMessageHandler());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://169.254.169.254/latest/meta-data/iam/"));

        AssertBlockedAddress(exception);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
        => new(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

    private static void AssertBlockedAddress(HttpRequestException exception)
    {
        var messages = $"{exception.Message} {exception.InnerException?.Message}";
        Assert.Contains(
            FeatureChangeWebhookUrlValidation.DisallowedAddressMessage,
            messages,
            StringComparison.Ordinal);
    }
}
