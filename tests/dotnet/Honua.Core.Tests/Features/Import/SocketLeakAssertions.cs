// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;

namespace Honua.Core.Tests.Features.Import;

internal static class SocketLeakAssertions
{
    internal static async Task CanceledConnectDoesNotLeakAsync(SocketsHttpHandler socketsHandler)
    {
        var connectCallback = socketsHandler.ConnectCallback;
        connectCallback.Should().NotBeNull();

        var baselineDescriptors = CountOpenFileDescriptors();

        for (var i = 0; i < 128; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
            var context = SocketConnectionContextFactory.Create(new DnsEndPoint("example.com", 443), request);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                connectCallback!(context, cts.Token).AsTask());
        }

        // Drain finalizers before measuring retained descriptors.
        _ = GC.GetTotalMemory(forceFullCollection: true);
        GC.WaitForPendingFinalizers();
        _ = GC.GetTotalMemory(forceFullCollection: true);

        var settledDescriptors = await WaitForDescriptorSettleAsync(
            baselineDescriptors,
            TimeSpan.FromSeconds(10));
        (settledDescriptors - baselineDescriptors).Should().BeLessThan(96);
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
}
