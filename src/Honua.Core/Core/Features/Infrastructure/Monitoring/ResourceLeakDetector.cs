// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

public interface IResourceLeakDetector
{
    void TrackResource(object resource, string resourceType, string? correlationId = null, string? allocationContext = null);

    void UntrackResource(object resource);

    ResourceTrackingStatistics GetStatistics();

    IReadOnlyList<ResourceLeakInfo> GetPotentialLeaks(TimeSpan? olderThan = null);

    Task<ResourceLeakScanResult> ScanForLeaksAsync();
}

public sealed class ResourceLeakDetectionOptions
{
    public bool Enabled { get; set; } = true;

    public bool CaptureAllocationStackTraces { get; set; }

    public int MaxStackTraceFrames { get; set; } = 10;

    public TimeSpan LeakSuspicionThreshold { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxTrackedResources { get; set; } = 10000;

    public TimeSpan AutoScanInterval { get; set; } = TimeSpan.FromMinutes(2);

    public bool EnableAutoScan { get; set; } = true;
}

public sealed record ResourceTrackingStatistics
{
    public int TrackedResources { get; init; }

    public long DisposedResources { get; init; }

    public int PotentialLeaks { get; init; }

    public IReadOnlyDictionary<string, int> ByResourceType { get; init; } =
        new Dictionary<string, int>();

    public TimeSpan? OldestResourceAge { get; init; }

    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
}

public sealed record ResourceLeakInfo
{
    public string ResourceType { get; init; } = string.Empty;

    public string? CorrelationId { get; init; }

    public string? AllocationContext { get; init; }

    public DateTime TrackedAt { get; init; }

    public TimeSpan Age { get; init; }

    public string? AllocationStackTrace { get; init; }

    public bool IsAlive { get; init; }

    public int ResourceHashCode { get; init; }
}

public sealed record ResourceLeakScanResult
{
    public int ResourcesScanned { get; init; }

    public int LeaksDetected { get; init; }

    public int ResourcesGarbageCollected { get; init; }

    public IReadOnlyList<ResourceLeakInfo> DetectedLeaks { get; init; } = Array.Empty<ResourceLeakInfo>();

    public TimeSpan ScanDuration { get; init; }

    public DateTime ScannedAt { get; init; } = DateTime.UtcNow;
}

internal sealed partial class ResourceLeakDetector : IResourceLeakDetector, IHostedService, IDisposable
{
    private readonly ILogger<ResourceLeakDetector> _logger;
    private readonly IHostEnvironment _environment;
    private readonly ResourceLeakDetectionOptions _options;

    private readonly ConcurrentDictionary<int, TrackedResource> _trackedResources = new();
    private readonly Timer? _autoScanTimer;
    private long _disposedResourceCount;
    private volatile bool _disposed;

    public ResourceLeakDetector(
        ILogger<ResourceLeakDetector> logger,
        IHostEnvironment environment,
        IOptions<ResourceLeakDetectionOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        // Only enable in development/test environments
        if (_options.Enabled && (_environment.IsDevelopment() || IsTestEnvironment()) && _options.EnableAutoScan)
        {
            _autoScanTimer = new Timer(
                AutoScanCallback,
                null,
                _options.AutoScanInterval,
                _options.AutoScanInterval);
        }
    }

    public void TrackResource(object resource, string resourceType, string? correlationId = null, string? allocationContext = null)
    {
        if (_disposed || !ShouldTrack()) return;

        ArgumentNullException.ThrowIfNull(resource);

        if (_trackedResources.Count >= _options.MaxTrackedResources)
        {
            ResourceLog.MaxTrackedResourcesExceeded(_logger, _options.MaxTrackedResources);
            return;
        }

        var resourceHashCode = RuntimeHelpers.GetHashCode(resource);
        var weakRef = new WeakReference(resource);

        var trackedResource = new TrackedResource
        {
            ResourceType = resourceType,
            CorrelationId = correlationId,
            AllocationContext = allocationContext ?? GetAllocationContext(),
            TrackedAt = DateTime.UtcNow,
            WeakReference = weakRef,
            ResourceHashCode = resourceHashCode,
            AllocationStackTrace = _options.CaptureAllocationStackTraces ? CaptureStackTrace() : null
        };

        _trackedResources.TryAdd(resourceHashCode, trackedResource);
    }

    public void UntrackResource(object resource)
    {
        if (_disposed || !ShouldTrack() || resource == null) return;

        var resourceHashCode = RuntimeHelpers.GetHashCode(resource);
        if (_trackedResources.TryRemove(resourceHashCode, out _))
        {
            Interlocked.Increment(ref _disposedResourceCount);
        }
    }

    public ResourceTrackingStatistics GetStatistics()
    {
        if (_disposed || !ShouldTrack())
        {
            return new ResourceTrackingStatistics();
        }

        var now = DateTime.UtcNow;
        var leakThreshold = now.Subtract(_options.LeakSuspicionThreshold);

        var byType = new Dictionary<string, int>();
        TimeSpan? oldestAge = null;
        var potentialLeaks = 0;

        foreach (var tracked in _trackedResources.Values)
        {
            var age = now - tracked.TrackedAt;
            if (!oldestAge.HasValue || age > oldestAge.Value)
                oldestAge = age;

            if (tracked.TrackedAt < leakThreshold)
                potentialLeaks++;

            byType[tracked.ResourceType] = byType.GetValueOrDefault(tracked.ResourceType, 0) + 1;
        }

        return new ResourceTrackingStatistics
        {
            TrackedResources = _trackedResources.Count,
            DisposedResources = Interlocked.Read(ref _disposedResourceCount),
            PotentialLeaks = potentialLeaks,
            ByResourceType = byType,
            OldestResourceAge = oldestAge
        };
    }

    public IReadOnlyList<ResourceLeakInfo> GetPotentialLeaks(TimeSpan? olderThan = null)
    {
        if (_disposed || !ShouldTrack())
            return Array.Empty<ResourceLeakInfo>();

        var threshold = olderThan ?? _options.LeakSuspicionThreshold;
        var cutoffTime = DateTime.UtcNow.Subtract(threshold);

        var leaks = _trackedResources.Values
            .Where(tracked => tracked.TrackedAt < cutoffTime)
            .Select(tracked => new ResourceLeakInfo
            {
                ResourceType = tracked.ResourceType,
                CorrelationId = tracked.CorrelationId,
                AllocationContext = tracked.AllocationContext,
                TrackedAt = tracked.TrackedAt,
                Age = DateTime.UtcNow - tracked.TrackedAt,
                AllocationStackTrace = tracked.AllocationStackTrace,
                IsAlive = tracked.WeakReference.IsAlive,
                ResourceHashCode = tracked.ResourceHashCode
            });

        return leaks.OrderByDescending(l => l.Age).ToList();
    }

    public async Task<ResourceLeakScanResult> ScanForLeaksAsync()
    {
        if (_disposed || !ShouldTrack())
        {
            return new ResourceLeakScanResult();
        }

        var stopwatch = Stopwatch.StartNew();
        var resourcesScanned = 0;
        var resourcesGarbageCollected = 0;
        var detectedLeaks = new List<ResourceLeakInfo>();

        // Force garbage collection to clean up unreferenced resources
        await Task.Run(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced);
        });

        var leakThreshold = DateTime.UtcNow.Subtract(_options.LeakSuspicionThreshold);
        var resourcesToRemove = new List<int>();

        foreach (var kvp in _trackedResources)
        {
            resourcesScanned++;
            var tracked = kvp.Value;

            // Remove garbage collected resources
            if (!tracked.WeakReference.IsAlive)
            {
                resourcesToRemove.Add(kvp.Key);
                resourcesGarbageCollected++;
                continue;
            }

            // Check for potential leaks
            if (tracked.TrackedAt < leakThreshold)
            {
                var age = DateTime.UtcNow - tracked.TrackedAt;
                detectedLeaks.Add(new ResourceLeakInfo
                {
                    ResourceType = tracked.ResourceType,
                    CorrelationId = tracked.CorrelationId,
                    AllocationContext = tracked.AllocationContext,
                    TrackedAt = tracked.TrackedAt,
                    Age = age,
                    AllocationStackTrace = tracked.AllocationStackTrace,
                    IsAlive = true,
                    ResourceHashCode = tracked.ResourceHashCode
                });
            }
        }

        // Clean up garbage collected resources
        foreach (var hashCode in resourcesToRemove)
        {
            _trackedResources.TryRemove(hashCode, out _);
        }

        stopwatch.Stop();

        var result = new ResourceLeakScanResult
        {
            ResourcesScanned = resourcesScanned,
            LeaksDetected = detectedLeaks.Count,
            ResourcesGarbageCollected = resourcesGarbageCollected,
            DetectedLeaks = detectedLeaks,
            ScanDuration = stopwatch.Elapsed
        };

        if (detectedLeaks.Count > 0)
        {
            ResourceLog.ResourceLeaksDetected(_logger, detectedLeaks.Count, resourcesScanned);

            // Log details of the most concerning leaks
            foreach (var leak in detectedLeaks.OrderByDescending(l => l.Age).Take(5))
            {
                ResourceLog.ResourceLeakDetail(_logger, leak.ResourceType, leak.Age.TotalMinutes, leak.CorrelationId ?? "Unknown");
            }
        }

        return result;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (ShouldTrack())
        {
            ResourceLog.ResourceLeakDetectorStarted(_logger);
        }
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (ShouldTrack())
        {
            // Perform final leak scan
            var finalScan = await ScanForLeaksAsync();
            if (finalScan.LeaksDetected > 0)
            {
                ResourceLog.FinalLeakScanResults(_logger, finalScan.LeaksDetected, finalScan.ResourcesScanned);
            }
        }

        await Task.CompletedTask;
    }

    private bool ShouldTrack() =>
        _options.Enabled && (_environment.IsDevelopment() || IsTestEnvironment()) && !_disposed;

    private bool IsTestEnvironment() =>
        _environment.EnvironmentName.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
        _environment.EnvironmentName.Contains("Testing", StringComparison.OrdinalIgnoreCase);

    private void AutoScanCallback(object? state)
    {
        if (_disposed) return;

        // Fire-and-forget task with proper exception handling
        _ = Task.Run(async () =>
        {
            try
            {
                await ScanForLeaksAsync();
            }
            // Intentional broad catch: this is a fire-and-forget Timer callback with no caller
            // to observe an exception. Any failure must be logged and swallowed here rather than
            // becoming an unobserved task exception that could crash the process.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ResourceLog.AutoScanFailed(_logger, ex);
            }
        });
    }

    private static string GetAllocationContext()
    {
        return "Unknown";
    }

    private string? CaptureStackTrace()
    {
        if (!_options.CaptureAllocationStackTraces) return null;

        var stackTrace = Environment.StackTrace;
        if (_options.MaxStackTraceFrames <= 0)
        {
            return stackTrace;
        }

        var stackFrames = stackTrace
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(2)
            .Take(_options.MaxStackTraceFrames);
        return string.Join(Environment.NewLine, stackFrames);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoScanTimer?.Dispose();
        _trackedResources.Clear();
    }

    private sealed class TrackedResource
    {
        public string ResourceType { get; init; } = string.Empty;
        public string? CorrelationId { get; init; }
        public string? AllocationContext { get; init; }
        public DateTime TrackedAt { get; init; }
        public WeakReference WeakReference { get; init; } = null!;
        public int ResourceHashCode { get; init; }
        public string? AllocationStackTrace { get; init; }
    }

    private static partial class ResourceLog
    {
        [LoggerMessage(
            EventId = 9101,
            Level = LogLevel.Information,
            Message = "Resource leak detector started")]
        public static partial void ResourceLeakDetectorStarted(ILogger logger);

        [LoggerMessage(
            EventId = 9102,
            Level = LogLevel.Warning,
            Message = "Maximum tracked resources exceeded: {MaxResources}")]
        public static partial void MaxTrackedResourcesExceeded(ILogger logger, int maxResources);

        [LoggerMessage(
            EventId = 9103,
            Level = LogLevel.Warning,
            Message = "Resource leaks detected: {LeaksDetected} out of {ResourcesScanned} resources")]
        public static partial void ResourceLeaksDetected(ILogger logger, int leaksDetected, int resourcesScanned);

        [LoggerMessage(
            EventId = 9104,
            Level = LogLevel.Warning,
            Message = "Resource leak: {ResourceType} alive for {AgeMinutes:F1} minutes [CorrelationId={CorrelationId}]")]
        public static partial void ResourceLeakDetail(ILogger logger, string resourceType, double ageMinutes, string correlationId);

        [LoggerMessage(
            EventId = 9105,
            Level = LogLevel.Information,
            Message = "Final leak scan: {LeaksDetected} leaks detected out of {ResourcesScanned} resources")]
        public static partial void FinalLeakScanResults(ILogger logger, int leaksDetected, int resourcesScanned);

        [LoggerMessage(
            EventId = 9106,
            Level = LogLevel.Error,
            Message = "Auto scan for resource leaks failed")]
        public static partial void AutoScanFailed(ILogger logger, Exception exception);
    }
}

internal sealed class NullResourceLeakDetector : IResourceLeakDetector
{
    public void TrackResource(object resource, string resourceType, string? correlationId = null, string? allocationContext = null)
    {
        // No-op in production
    }

    public void UntrackResource(object resource)
    {
        // No-op in production
    }

    public ResourceTrackingStatistics GetStatistics() => new();

    public IReadOnlyList<ResourceLeakInfo> GetPotentialLeaks(TimeSpan? olderThan = null) =>
        Array.Empty<ResourceLeakInfo>();

    public Task<ResourceLeakScanResult> ScanForLeaksAsync() =>
        Task.FromResult(new ResourceLeakScanResult());
}
