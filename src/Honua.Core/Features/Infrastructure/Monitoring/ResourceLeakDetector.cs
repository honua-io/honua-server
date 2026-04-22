// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Detects resource leaks and tracks resource lifecycle for diagnostics in development/test environments.
/// Provides enhanced disposal pattern monitoring and memory allocation tracking.
/// </summary>
public interface IResourceLeakDetector
{
    /// <summary>
    /// Registers a resource for leak tracking.
    /// </summary>
    /// <param name="resource">The resource to track</param>
    /// <param name="resourceType">Type/category of the resource</param>
    /// <param name="correlationId">Correlation ID for tracking</param>
    /// <param name="allocationContext">Context where resource was allocated</param>
    void TrackResource(object resource, string resourceType, string? correlationId = null, string? allocationContext = null);

    /// <summary>
    /// Unregisters a resource when it's properly disposed.
    /// </summary>
    /// <param name="resource">The resource being disposed</param>
    void UntrackResource(object resource);

    /// <summary>
    /// Gets current resource tracking statistics.
    /// </summary>
    ResourceTrackingStatistics GetStatistics();

    /// <summary>
    /// Gets details of potentially leaked resources.
    /// </summary>
    IReadOnlyList<ResourceLeakInfo> GetPotentialLeaks(TimeSpan? olderThan = null);

    /// <summary>
    /// Forces garbage collection and checks for leaked resources.
    /// </summary>
    Task<ResourceLeakScanResult> ScanForLeaksAsync();
}

/// <summary>
/// Configuration options for resource leak detection.
/// </summary>
public sealed class ResourceLeakDetectionOptions
{
    /// <summary>
    /// Whether resource leak detection is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether to capture stack traces for resource allocations (expensive but useful for debugging).
    /// </summary>
    public bool CaptureAllocationStackTraces { get; set; }

    /// <summary>
    /// Maximum number of stack trace frames to capture.
    /// </summary>
    public int MaxStackTraceFrames { get; set; } = 10;

    /// <summary>
    /// Age threshold for considering resources as potentially leaked.
    /// </summary>
    public TimeSpan LeakSuspicionThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of resources to track simultaneously.
    /// </summary>
    public int MaxTrackedResources { get; set; } = 10000;

    /// <summary>
    /// Interval for automatic leak scanning.
    /// </summary>
    public TimeSpan AutoScanInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether to automatically scan for leaks in background.
    /// </summary>
    public bool EnableAutoScan { get; set; } = true;
}

/// <summary>
/// Statistics about resource tracking.
/// </summary>
public sealed record ResourceTrackingStatistics
{
    /// <summary>
    /// Total number of resources currently being tracked.
    /// </summary>
    public int TrackedResources { get; init; }

    /// <summary>
    /// Number of resources that have been properly disposed.
    /// </summary>
    public long DisposedResources { get; init; }

    /// <summary>
    /// Number of potential resource leaks detected.
    /// </summary>
    public int PotentialLeaks { get; init; }

    /// <summary>
    /// Resources by type.
    /// </summary>
    public IReadOnlyDictionary<string, int> ByResourceType { get; init; } =
        new Dictionary<string, int>();

    /// <summary>
    /// Age of the oldest tracked resource.
    /// </summary>
    public TimeSpan? OldestResourceAge { get; init; }

    /// <summary>
    /// Timestamp when statistics were collected.
    /// </summary>
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Information about a potentially leaked resource.
/// </summary>
public sealed record ResourceLeakInfo
{
    /// <summary>
    /// Type/category of the resource.
    /// </summary>
    public string ResourceType { get; init; } = string.Empty;

    /// <summary>
    /// Correlation ID associated with the resource.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Context where the resource was allocated.
    /// </summary>
    public string? AllocationContext { get; init; }

    /// <summary>
    /// When the resource was first tracked.
    /// </summary>
    public DateTime TrackedAt { get; init; }

    /// <summary>
    /// Age of the resource.
    /// </summary>
    public TimeSpan Age { get; init; }

    /// <summary>
    /// Stack trace from allocation (if captured).
    /// </summary>
    public string? AllocationStackTrace { get; init; }

    /// <summary>
    /// Whether the resource object is still alive in memory.
    /// </summary>
    public bool IsAlive { get; init; }

    /// <summary>
    /// Hash code of the original resource object.
    /// </summary>
    public int ResourceHashCode { get; init; }
}

/// <summary>
/// Result of a resource leak scan.
/// </summary>
public sealed record ResourceLeakScanResult
{
    /// <summary>
    /// Number of resources scanned.
    /// </summary>
    public int ResourcesScanned { get; init; }

    /// <summary>
    /// Number of leaked resources detected.
    /// </summary>
    public int LeaksDetected { get; init; }

    /// <summary>
    /// Number of resources that were garbage collected.
    /// </summary>
    public int ResourcesGarbageCollected { get; init; }

    /// <summary>
    /// Details of detected leaks.
    /// </summary>
    public IReadOnlyList<ResourceLeakInfo> DetectedLeaks { get; init; } = Array.Empty<ResourceLeakInfo>();

    /// <summary>
    /// Duration of the leak scan.
    /// </summary>
    public TimeSpan ScanDuration { get; init; }

    /// <summary>
    /// When the scan was performed.
    /// </summary>
    public DateTime ScannedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Production implementation of resource leak detection (disabled in production, active in development/test).
/// </summary>
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
        if (_options.Enabled && (_environment.IsDevelopment() || IsTestEnvironment()))
        {
            if (_options.EnableAutoScan)
            {
                _autoScanTimer = new Timer(
                    AutoScanCallback,
                    null,
                    _options.AutoScanInterval,
                    _options.AutoScanInterval);
            }
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

        var leaks = new List<ResourceLeakInfo>();

        foreach (var tracked in _trackedResources.Values)
        {
            if (tracked.TrackedAt < cutoffTime)
            {
                var age = DateTime.UtcNow - tracked.TrackedAt;
                leaks.Add(new ResourceLeakInfo
                {
                    ResourceType = tracked.ResourceType,
                    CorrelationId = tracked.CorrelationId,
                    AllocationContext = tracked.AllocationContext,
                    TrackedAt = tracked.TrackedAt,
                    Age = age,
                    AllocationStackTrace = tracked.AllocationStackTrace,
                    IsAlive = tracked.WeakReference.IsAlive,
                    ResourceHashCode = tracked.ResourceHashCode
                });
            }
        }

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
            catch (Exception ex)
            {
                ResourceLog.AutoScanFailed(_logger, ex);
            }
        });
    }

    private static string GetAllocationContext()
    {
        var frame = new StackFrame(2, false); // Skip TrackResource and immediate caller
        return frame.GetMethod()?.DeclaringType?.FullName ?? "Unknown";
    }

    private string? CaptureStackTrace()
    {
        if (!_options.CaptureAllocationStackTraces) return null;

        var stackTrace = new StackTrace(2, true); // Skip TrackResource and immediate caller
        var frames = stackTrace.GetFrames();

        if (frames == null) return null;

        var sb = new StringBuilder();
        var frameCount = Math.Min(frames.Length, _options.MaxStackTraceFrames);

        for (int i = 0; i < frameCount; i++)
        {
            var frame = frames[i];
            var method = frame.GetMethod();

            if (method != null)
            {
                sb.Append("   at ");
                sb.Append(method.DeclaringType?.FullName);
                sb.Append('.');
                sb.AppendLine(method.Name);
            }
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoScanTimer?.Dispose();
        _trackedResources.Clear();
    }

    /// <summary>
    /// Internal data structure for tracked resources.
    /// </summary>
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

/// <summary>
/// Null implementation for production environments where leak detection is disabled.
/// </summary>
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
