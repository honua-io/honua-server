// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

public sealed class ResourceLeakDetectorScanTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ScanForLeaksAsync_WithNothingTracked_ReturnsEmptyResult()
    {
        using var detector = CreateDetector();

        var result = await detector.ScanForLeaksAsync();

        Assert.Equal(0, result.ResourcesScanned);
        Assert.Equal(0, result.LeaksDetected);
        Assert.Equal(0, result.ResourcesGarbageCollected);
        Assert.Empty(result.DetectedLeaks);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ScanForLeaksAsync_WithTrackedResources_ReportsLiveLeaksAndCollectedResources()
    {
        using var detector = CreateDetector();
        var live = new object();
        detector.TrackResource(live, "live-resource", correlationId: "corr-live");
        TrackUnreachableResource(detector);

        var result = await detector.ScanForLeaksAsync();

        Assert.Equal(2, result.ResourcesScanned);
        Assert.Equal(1, result.ResourcesGarbageCollected);
        var leak = Assert.Single(result.DetectedLeaks);
        Assert.Equal("live-resource", leak.ResourceType);
        Assert.Equal("corr-live", leak.CorrelationId);
        GC.KeepAlive(live);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ScanForLeaksAsync_AfterUntrackingEveryResource_ReturnsEmptyResult()
    {
        using var detector = CreateDetector();
        var resource = new object();
        detector.TrackResource(resource, "released-resource");
        detector.UntrackResource(resource);

        var result = await detector.ScanForLeaksAsync();

        Assert.Equal(0, result.ResourcesScanned);
        Assert.Equal(0, result.LeaksDetected);
        Assert.Equal(1, detector.GetStatistics().DisposedResources);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TrackUnreachableResource(ResourceLeakDetector detector)
        => detector.TrackResource(new object(), "unreachable-resource");

    private static ResourceLeakDetector CreateDetector()
        => new(
            NullLogger<ResourceLeakDetector>.Instance,
            new TestHostEnvironment(),
            Options.Create(new ResourceLeakDetectionOptions
            {
                Enabled = true,
                EnableAutoScan = false,
                LeakSuspicionThreshold = TimeSpan.Zero,
            }));

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
