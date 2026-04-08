// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Text;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class ArcGisRestClientSecurityTests
{
    [Fact]
    public async Task DiscoverServiceAsync_HttpScheme_ThrowsWithoutSendingRequest()
    {
        var handler = new CountingHandler();
        var client = CreateClient(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.DiscoverServiceAsync(
                "http://example.com/arcgis/rest/services/Test/FeatureServer",
                timeoutSeconds: 5,
                maxRetries: 0,
                CancellationToken.None));

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverServiceAsync_PrivateResolution_ThrowsWithoutSendingRequest()
    {
        var handler = new CountingHandler();
        var client = CreateClient(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") }));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.DiscoverServiceAsync(
                "https://example.com/arcgis/rest/services/Test/FeatureServer",
                timeoutSeconds: 5,
                maxRetries: 0,
                CancellationToken.None));

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverServiceAsync_PublicResolution_SendsRequest()
    {
        var handler = new CountingHandler("""
            {
              "serviceDescription": "Test Service",
              "layers": []
            }
            """);
        var client = CreateClient(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var result = await client.DiscoverServiceAsync(
            "https://example.com/arcgis/rest/services/Test/FeatureServer",
            timeoutSeconds: 5,
            maxRetries: 0,
            CancellationToken.None);

        result.ServiceName.Should().Be("Test Service");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAllowedAddressesAsync_MixedPublicAndPrivate_Throws()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ArcGisRestClient.ResolveAllowedAddressesAsync(
                "example.com",
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5") }),
                CancellationToken.None));

        ex.Message.Should().Contain("disallowed network address");
    }

    [Fact]
    public async Task ResolveAllowedAddressesAsync_PublicIpLiteral_ReturnsAddress()
    {
        var addresses = await ArcGisRestClient.ResolveAllowedAddressesAsync(
            "93.184.216.34",
            (_, _) => throw new InvalidOperationException("Resolver should not be called for literals."),
            CancellationToken.None);

        addresses.Should().ContainSingle();
        addresses[0].Should().Be(IPAddress.Parse("93.184.216.34"));
    }

    [Fact]
    public void CreatePinnedDnsHttpMessageHandler_ConfiguresConnectCallback()
    {
        var handler = ArcGisRestClient.CreatePinnedDnsHttpMessageHandler();

        var socketsHandler = handler.Should().BeOfType<SocketsHttpHandler>().Subject;
        socketsHandler.ConnectCallback.Should().NotBeNull();
    }

    [Fact]
    public async Task CreatePinnedDnsHttpMessageHandler_CanceledConnect_DoesNotLeakSockets()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc/self/fd"))
        {
            return;
        }

        var handler = ArcGisRestClient.CreatePinnedDnsHttpMessageHandler((_, _) =>
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

        // Drain pending socket finalizers so the FD count reflects actual leaks rather
        // than queued finalizable state from cancelled ConnectAsync calls. Slow CI runners
        // were showing >64 transient descriptors without this nudge even though the
        // production code in ConnectWithPinnedDnsAsync deterministically disposes the
        // socket on the cancellation path.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var settledDescriptors = await WaitForDescriptorSettleAsync(
            baselineDescriptors,
            TimeSpan.FromSeconds(10));
        (settledDescriptors - baselineDescriptors).Should().BeLessThan(96);
    }

    private static ArcGisRestClient CreateClient(
        HttpMessageHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver)
    {
        var httpClient = new HttpClient(handler);
        return new ArcGisRestClient(httpClient, NullLogger<ArcGisRestClient>.Instance, resolver);
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
