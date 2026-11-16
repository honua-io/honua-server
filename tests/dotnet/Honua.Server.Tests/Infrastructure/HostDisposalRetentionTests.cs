// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure;

// Disposed in-process hosts must become collectable. Anything that outlives a host and holds
// its graph (a console logger queue thread that captured the WebApplicationFactory startup
// AsyncLocal, or an IHttpClientFactory expired-handler cleanup timer) keeps every disposed host
// alive, so each later host in the test process boots and stops more slowly. The class runs in
// the non-parallel host cleanup collection so no concurrent host can start a process-wide
// capture while the measured hosts boot.
[Collection(SharedHostCleanupGroup.Name)]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class HostDisposalRetentionTests
{
    // OpenTelemetry's process-wide self-diagnostics refresher captures the execution context of
    // whichever early host first starts it and holds it for the life of the process. That is a
    // fixed cost, so warm-up hosts absorb it and the assertion covers only the hosts after them.
    private const int WarmUpHosts = 2;
    private const int MeasuredHosts = 3;
    private static readonly TimeSpan CollectionDeadline = TimeSpan.FromSeconds(15);

    [IntegrationTest]
    public async Task DisposeAsync_RepeatedHosts_ReleasesEachHostsServices()
    {
        for (var index = 0; index < WarmUpHosts; index++)
        {
            await BootServeAndDisposeAsync();
        }

        var measured = new List<WeakReference>();
        for (var index = 0; index < MeasuredHosts; index++)
        {
            measured.Add(await BootServeAndDisposeAsync());
        }

        var deadline = DateTime.UtcNow + CollectionDeadline;
        while (measured.Exists(static host => host.IsAlive) && DateTime.UtcNow < deadline)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }

        var retained = measured.Count(static host => host.IsAlive);
        Assert.True(retained == 0, $"{retained} of {MeasuredHosts} disposed hosts are still reachable after garbage collection.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> BootServeAndDisposeAsync()
    {
        var factory = new TestWebApplicationFactory();
        try
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/healthz/live");
            Assert.True(response.IsSuccessStatusCode, $"Health probe returned {(int)response.StatusCode}.");
            return new WeakReference(factory.Services);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
