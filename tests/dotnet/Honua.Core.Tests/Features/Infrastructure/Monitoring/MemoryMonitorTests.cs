// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Unit tests for MemoryMonitor functionality.
/// </summary>
public class MemoryMonitorTests
{
    [Fact]
    public void GetMemoryUsage_ShouldReturnValidData()
    {
        // Act
        var memoryUsage = MemoryMonitor.GetMemoryUsage();

        // Assert
        Assert.True(memoryUsage.AllocatedBytes > 0, "Allocated bytes should be greater than 0");
        Assert.True(memoryUsage.HeapSizeBytes >= 0, "Heap size should be non-negative");
        Assert.True(memoryUsage.TotalAvailableMemoryBytes > 0, "Total available memory should be greater than 0");
        Assert.True(memoryUsage.Gen0Collections >= 0, "Gen0 collections should be non-negative");
        Assert.True(memoryUsage.Gen1Collections >= 0, "Gen1 collections should be non-negative");
        Assert.True(memoryUsage.Gen2Collections >= 0, "Gen2 collections should be non-negative");
        Assert.True(memoryUsage.Timestamp != default, "Timestamp should be set");
    }

    [Fact]
    public void GetAllocatedMemory_ShouldReturnPositiveValue()
    {
        // Act
        var allocatedMemory = MemoryMonitor.GetAllocatedMemory();

        // Assert
        Assert.True(allocatedMemory > 0, "Allocated memory should be positive");
    }

    [Fact]
    public void GetAllocatedMemory_WithForceCollection_ShouldReturnValue()
    {
        // Act
        var allocatedMemory = MemoryMonitor.GetAllocatedMemory(forceFullCollection: true);

        // Assert
        Assert.True(allocatedMemory > 0, "Allocated memory should be positive after forced collection");
    }

    [Fact]
    public void ForceGarbageCollectionAndMeasure_ShouldReturnValidData()
    {
        // Arrange - Create some objects to be collected
        var objects = new List<byte[]>();
        for (int i = 0; i < 1000; i++)
        {
            objects.Add(new byte[1024]);
        }
        objects.Clear(); // Make objects eligible for collection

        // Act
        var memoryUsage = MemoryMonitor.ForceGarbageCollectionAndMeasure();

        // Assert
        Assert.True(memoryUsage.AllocatedBytes > 0, "Allocated bytes should be greater than 0");
        Assert.True(memoryUsage.Timestamp != default, "Timestamp should be set");
    }

    [Fact]
    public void CalculateMemoryPressure_WithValidData_ShouldReturnCorrectPercentage()
    {
        // Arrange
        var memoryUsage = new MemoryUsage
        {
            AllocatedBytes = 100 * 1024 * 1024, // 100MB
            Gen0Collections = 10,
            Gen1Collections = 5,
            Gen2Collections = 2,
            HeapSizeBytes = 120 * 1024 * 1024, // 120MB
            HighMemoryLoadThresholdBytes = 8L * 1024 * 1024 * 1024, // 8GB
            MemoryLoadBytes = 4L * 1024 * 1024 * 1024, // 4GB
            TotalAvailableMemoryBytes = 16L * 1024 * 1024 * 1024, // 16GB
            Timestamp = DateTimeOffset.UtcNow
        };

        // Act
        var pressure = MemoryMonitor.CalculateMemoryPressure(memoryUsage);

        // Assert
        Assert.True(pressure >= 0 && pressure <= 100, $"Memory pressure should be 0-100%, got {pressure:F2}%");
        Assert.Equal(25.0, pressure, 1); // 4GB / 16GB = 25%
    }

    [Fact]
    public void CalculateMemoryPressure_WithZeroAvailableMemory_ShouldReturnZero()
    {
        // Arrange
        var memoryUsage = new MemoryUsage
        {
            AllocatedBytes = 100 * 1024 * 1024,
            Gen0Collections = 0,
            Gen1Collections = 0,
            Gen2Collections = 0,
            HeapSizeBytes = 100 * 1024 * 1024,
            HighMemoryLoadThresholdBytes = 1024 * 1024 * 1024,
            MemoryLoadBytes = 512 * 1024 * 1024,
            TotalAvailableMemoryBytes = 0, // Zero available memory
            Timestamp = DateTimeOffset.UtcNow
        };

        // Act
        var pressure = MemoryMonitor.CalculateMemoryPressure(memoryUsage);

        // Assert
        Assert.Equal(0.0, pressure);
    }

    [Fact]
    public void MemoryUsage_MemoryPressurePercentage_ShouldCalculateCorrectly()
    {
        // Arrange
        var memoryUsage = new MemoryUsage
        {
            AllocatedBytes = 100 * 1024 * 1024,
            Gen0Collections = 5,
            Gen1Collections = 3,
            Gen2Collections = 1,
            HeapSizeBytes = 120 * 1024 * 1024,
            HighMemoryLoadThresholdBytes = 8L * 1024 * 1024 * 1024,
            MemoryLoadBytes = 2L * 1024 * 1024 * 1024, // 2GB
            TotalAvailableMemoryBytes = 8L * 1024 * 1024 * 1024, // 8GB
            Timestamp = DateTimeOffset.UtcNow
        };

        // Act & Assert
        Assert.Equal(25.0, memoryUsage.MemoryPressurePercentage, 1); // 2GB / 8GB = 25%
    }

    [Fact]
    public void MemoryUsage_IsHighMemoryPressure_ShouldReturnCorrectValue()
    {
        // Arrange - High pressure (85%)
        var highPressureUsage = new MemoryUsage
        {
            AllocatedBytes = 1024 * 1024 * 1024,
            Gen0Collections = 100,
            Gen1Collections = 50,
            Gen2Collections = 25,
            HeapSizeBytes = 1200 * 1024 * 1024,
            HighMemoryLoadThresholdBytes = 8L * 1024 * 1024 * 1024,
            MemoryLoadBytes = 6800L * 1024 * 1024, // 6.8GB
            TotalAvailableMemoryBytes = 8L * 1024 * 1024 * 1024, // 8GB
            Timestamp = DateTimeOffset.UtcNow
        };

        // Arrange - Low pressure (10%)
        var lowPressureUsage = new MemoryUsage
        {
            AllocatedBytes = 100 * 1024 * 1024,
            Gen0Collections = 10,
            Gen1Collections = 5,
            Gen2Collections = 2,
            HeapSizeBytes = 120 * 1024 * 1024,
            HighMemoryLoadThresholdBytes = 8L * 1024 * 1024 * 1024,
            MemoryLoadBytes = 800L * 1024 * 1024, // 0.8GB
            TotalAvailableMemoryBytes = 8L * 1024 * 1024 * 1024, // 8GB
            Timestamp = DateTimeOffset.UtcNow
        };

        // Act & Assert
        Assert.True(highPressureUsage.IsHighMemoryPressure, "Should detect high memory pressure");
        Assert.False(lowPressureUsage.IsHighMemoryPressure, "Should not detect high memory pressure for low usage");
    }

    [Fact]
    public void MemoryUsage_TotalGCCollections_ShouldSumAllGenerations()
    {
        // Arrange
        var memoryUsage = new MemoryUsage
        {
            AllocatedBytes = 100 * 1024 * 1024,
            Gen0Collections = 15,
            Gen1Collections = 10,
            Gen2Collections = 3,
            HeapSizeBytes = 120 * 1024 * 1024,
            HighMemoryLoadThresholdBytes = 8L * 1024 * 1024 * 1024,
            MemoryLoadBytes = 2L * 1024 * 1024 * 1024,
            TotalAvailableMemoryBytes = 8L * 1024 * 1024 * 1024,
            Timestamp = DateTimeOffset.UtcNow
        };

        // Act & Assert
        Assert.Equal(28, memoryUsage.TotalGCCollections); // 15 + 10 + 3 = 28
    }

    [Fact]
    public void GetMemoryUsage_ShouldTimestampMeasurementWithinCallWindow()
    {
        var before = DateTimeOffset.UtcNow;
        var usage = MemoryMonitor.GetMemoryUsage();
        var after = DateTimeOffset.UtcNow;

        Assert.True(usage.Timestamp >= before, "Measurement timestamp should not precede the call.");
        Assert.True(usage.Timestamp <= after, "Measurement timestamp should not be later than the completed call.");
    }
}
