// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class GeoServerRestClientSecurityTests
{
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

    [Fact]
    public void CreatePinnedDnsHttpMessageHandler_ConfiguresConnectCallback()
    {
        var handler = GeoServerRestClient.CreatePinnedDnsHttpMessageHandler();

        var socketsHandler = handler.Should().BeOfType<SocketsHttpHandler>().Subject;
        socketsHandler.ConnectCallback.Should().NotBeNull();
    }

    [LinuxProcFdFact]
    public async Task CreatePinnedDnsHttpMessageHandler_CanceledConnect_DoesNotLeakSockets()
    {
        var handler = GeoServerRestClient.CreatePinnedDnsHttpMessageHandler((_, _) =>
            Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var socketsHandler = handler.Should().BeOfType<SocketsHttpHandler>().Subject;
        var connectCallback = socketsHandler.ConnectCallback;
        connectCallback.Should().NotBeNull();

        var baselineDescriptors = CountOpenFileDescriptors();

        for (var i = 0; i < 128; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
            var context = CreateConnectionContext(new DnsEndPoint("example.com", 443), request);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                connectCallback!(context, cts.Token).AsTask());
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var settledDescriptors = await WaitForDescriptorSettleAsync(
            baselineDescriptors,
            TimeSpan.FromSeconds(10));
        (settledDescriptors - baselineDescriptors).Should().BeLessThan(96);
    }

    private static GeoServerRestClient CreateClient(
        HttpMessageHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver)
    {
        var httpClient = new HttpClient(handler);
        return new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance, resolver);
    }

    private static SocketsHttpConnectionContext CreateConnectionContext(DnsEndPoint dnsEndPoint, HttpRequestMessage request)
    {
        var constructor = typeof(SocketsHttpConnectionContext).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(DnsEndPoint), typeof(HttpRequestMessage)],
            modifiers: null);

        constructor.Should().NotBeNull();
        return (SocketsHttpConnectionContext)constructor!.Invoke([dnsEndPoint, request]);
    }

    private static async Task<int> WaitForDescriptorSettleAsync(int baselineDescriptors, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var lowestObserved = int.MaxValue;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = CountOpenFileDescriptors();
            if (current < lowestObserved)
            {
                lowestObserved = current;
            }

            if (current <= baselineDescriptors + 16)
            {
                return current;
            }

            await Task.Delay(100);
        }

        return lowestObserved;
    }

    private static int CountOpenFileDescriptors() => Directory.GetFiles("/proc/self/fd").Length;

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _jsonResponse;
        private int _requestCount;

        public CountingHandler(string jsonResponse = "{}")
        {
            _jsonResponse = jsonResponse;
        }

        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jsonResponse, Encoding.UTF8, "application/json")
            });
        }
    }
}
