// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class GeoServerRestClientSecurityTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public async Task DiscoverServiceAsync_PrivateResolution_ThrowsWithoutSendingRequest()
    {
        var handler = new CountingHandler();
        var client = CreateClient(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") }));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.DiscoverServiceAsync(
                "https://example.com/geoserver/rest",
                username: null,
                password: null,
                includeCompatibilityAnalysis: false,
                includeStyleContent: false,
                timeoutSeconds: 5,
                maxRetryAttempts: 0,
                allowUnsafeLocalUrls: false,
                CancellationToken.None));

        handler.RequestCount.Should().Be(0);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ResolveAllowedAddressesAsync_MixedPublicAndPrivate_Throws()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            GeoServerRestClient.ResolveAllowedAddressesAsync(
                "example.com",
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5") }),
                allowUnsafeLocalUrls: false,
                CancellationToken.None));

        ex.Message.Should().Contain("disallowed network address");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ResolveAllowedAddressesAsync_LoopbackAllowedForUnsafeLocalUrls_ReturnsAddress()
    {
        var addresses = await GeoServerRestClient.ResolveAllowedAddressesAsync(
            "127.0.0.1",
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
            allowUnsafeLocalUrls: true,
            CancellationToken.None);

        addresses.Should().Equal(IPAddress.Loopback);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void CreatePinnedDnsHttpMessageHandler_ConfiguresConnectCallback()
    {
        var handler = GeoServerRestClient.CreatePinnedDnsHttpMessageHandler();

        var socketsHandler = handler.Should().BeOfType<SocketsHttpHandler>().Subject;
        socketsHandler.ConnectCallback.Should().NotBeNull();
    }

    [Trait("Tier", "Fast")]
    [LinuxProcFdFact]
    public async Task CreatePinnedDnsHttpMessageHandler_CanceledConnect_DoesNotLeakSockets()
    {
        var handler = GeoServerRestClient.CreatePinnedDnsHttpMessageHandler((_, _) =>
            Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var socketsHandler = handler.Should().BeOfType<SocketsHttpHandler>().Subject;
        await SocketLeakAssertions.CanceledConnectDoesNotLeakAsync(socketsHandler);
    }

    private static GeoServerRestClient CreateClient(
        HttpMessageHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver)
    {
        var httpClient = new HttpClient(handler);
        return new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance, resolver);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _jsonResponse;
        private int _requestCount;

        public CountingHandler(string jsonResponse = "{}")
        {
            _jsonResponse = jsonResponse;
        }

        public int RequestCount => _requestCount;

        // Response ownership transfers to the caller via the return value
        // (HttpClient's pipeline disposes it); nothing leaks here.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult<System.Net.Http.HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jsonResponse, Encoding.UTF8, "application/json")
            });
        }
    }
}
