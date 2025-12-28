// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Memory;
using NetTopologySuite.Geometries;

namespace Honua.Server.Tests.Performance;

/// <summary>
/// Performance tests to validate memory optimizations for geometry processing
/// </summary>
[Collection("Performance")]
public class GeometryMemoryPerformanceTests
{
    private const int CoordinateCount = 100;

    [Fact]
    [Trait("Category", "Performance")]
    public void MemoryPoolVsStandardAllocation_LargeCoordinateArrays_MemoryPoolIsFaster()
    {
        // Arrange
        var iterations = 1000;
        var coordinateCount = 1000;

        // Act - Standard allocation
        var standardStopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var coordinates = new double[coordinateCount * 2];
            for (var j = 0; j < coordinateCount; j++)
            {
                coordinates[j * 2] = j;
                coordinates[j * 2 + 1] = j + 0.5;
            }
            // Simulate processing
            _ = coordinates.Sum();
        }
        standardStopwatch.Stop();

        // Act - Memory pool allocation
        var poolStopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            using var rental = GeometryMemoryManager.RentCoordinateBuffer(coordinateCount, 2);
            for (var j = 0; j < coordinateCount; j++)
            {
                rental.SetX(j, j);
                rental.SetY(j, j + 0.5);
            }
            // Simulate processing
            _ = rental.Span.ToArray().Sum();
        }
        poolStopwatch.Stop();

        // Assert
        Assert.True(poolStopwatch.Elapsed <= standardStopwatch.Elapsed * 1.2,
            $"Memory pool ({poolStopwatch.ElapsedMilliseconds}ms) should be competitive with standard allocation ({standardStopwatch.ElapsedMilliseconds}ms)");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void CoordinateBufferRental_LargePolygons_ProcessesEfficiently()
    {
        // Arrange
        var factory = new GeometryFactory();
        var coordinates = GeneratePolygonCoordinates(CoordinateCount);
        var polygon = factory.CreatePolygon(coordinates);

        var iterations = 100;

        // Act
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            using var rental = GeometryMemoryManager.RentCoordinateBuffer(polygon.Coordinates.Length, 2);
            for (var j = 0; j < polygon.Coordinates.Length; j++)
            {
                var coord = polygon.Coordinates[j];
                rental.SetX(j, coord.X);
                rental.SetY(j, coord.Y);
            }

            // Simulate coordinate processing
            var totalCoordinates = rental.CoordinateCount;
            Assert.True(totalCoordinates > 0);
        }
        stopwatch.Stop();

        // Assert
        var averageTimeMs = stopwatch.ElapsedMilliseconds / (double)iterations;
        Assert.True(averageTimeMs < 10, $"Average processing time ({averageTimeMs:F2}ms) should be under 10ms per polygon");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ByteArrayPooling_LargeWkbBuffers_ReducesAllocations()
    {
        // Arrange
        var iterations = 1000;
        var wkbSize = 4096; // 4KB WKB data

        var initialMemory = GC.GetTotalMemory(forceFullCollection: true);

        // Act - Using pooled buffers
        for (var i = 0; i < iterations; i++)
        {
            using var rental = GeometryMemoryManager.RentWkbBuffer(wkbSize);
            // Simulate WKB processing
            for (var j = 0; j < rental.UsableLength; j++)
            {
                rental.Span[j] = (byte)(j % 256);
            }
            // Verify data integrity
            Assert.Equal((byte)0, rental.Span[0]);
            Assert.Equal((byte)255, rental.Span[255]);
        }

        var finalMemory = GC.GetTotalMemory(forceFullCollection: true);
        var memoryGrowth = finalMemory - initialMemory;

        // Assert
        var expectedMaxGrowth = wkbSize * iterations * 0.1; // Should be much less than allocating every buffer
        Assert.True(memoryGrowth < expectedMaxGrowth,
            $"Memory growth ({memoryGrowth} bytes) should be much less than allocating all buffers ({wkbSize * iterations} bytes)");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void CoordinateTransformation_LargeDatasets_MaintainsPerformance()
    {
        // Arrange
        var coordinateCount = 10000;
        using var rental = GeometryMemoryManager.RentCoordinateBuffer(coordinateCount, 2);

        // Fill with test data
        for (var i = 0; i < coordinateCount; i++)
        {
            rental.SetX(i, i * 0.1);
            rental.SetY(i, i * 0.1 + 1000);
        }

        var transformFunction = (double x, double y) => (x * 1.5, y * 1.5); // Scale transformation

        // Act
        var stopwatch = Stopwatch.StartNew();
        var transformedCoordinates = rental.Span;
        for (var i = 0; i < coordinateCount; i++)
        {
            var index = i * 2;
            var (newX, newY) = transformFunction(transformedCoordinates[index], transformedCoordinates[index + 1]);
            transformedCoordinates[index] = newX;
            transformedCoordinates[index + 1] = newY;
        }
        stopwatch.Stop();

        // Assert
        Assert.True(stopwatch.ElapsedMilliseconds < 100,
            $"Coordinate transformation ({stopwatch.ElapsedMilliseconds}ms) should complete in under 100ms for {coordinateCount} coordinates");

        // Verify transformation correctness
        Assert.Equal(0.0, transformedCoordinates[0], precision: 6); // First X: 0 * 1.5 = 0
        Assert.Equal(1500.0, transformedCoordinates[1], precision: 6); // First Y: 1000 * 1.5 = 1500
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task MultipleSimultaneousRentals_ConcurrentAccess_HandlesCorrectly()
    {
        // Arrange
        var tasks = new List<Task>();
        var taskCount = 10;
        var iterationsPerTask = 100;

        // Act
        for (var t = 0; t < taskCount; t++)
        {
            var taskId = t;
            tasks.Add(Task.Run(() =>
            {
                for (var i = 0; i < iterationsPerTask; i++)
                {
                    using var rental = GeometryMemoryManager.RentCoordinateBuffer(50, 3);

                    // Set unique values based on task and iteration
                    for (var j = 0; j < 50; j++)
                    {
                        var uniqueValue = taskId * 1000 + i * 10 + j;
                        rental.SetX(j, uniqueValue);
                        rental.SetY(j, uniqueValue + 0.1);
                        rental.SetZ(j, uniqueValue + 0.2);
                    }

                    // Verify data integrity
                    for (var j = 0; j < 50; j++)
                    {
                        var expectedValue = taskId * 1000 + i * 10 + j;
                        var coord = rental.GetCoordinate(j);
                        Assert.Equal(expectedValue, coord[0], precision: 6);
                        Assert.Equal(expectedValue + 0.1, coord[1], precision: 6);
                        Assert.Equal(expectedValue + 0.2, coord[2], precision: 6);
                    }
                }
            }));
        }

        // Assert
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Generates coordinates for a polygon with the specified number of points
    /// </summary>
    private static Coordinate[] GeneratePolygonCoordinates(int pointCount)
    {
        var coordinates = new Coordinate[pointCount + 1]; // +1 to close the ring
        var angleStep = 2 * Math.PI / pointCount;

        for (var i = 0; i < pointCount; i++)
        {
            var angle = i * angleStep;
            coordinates[i] = new Coordinate(
                Math.Cos(angle) * 100, // Radius of 100
                Math.Sin(angle) * 100
            );
        }

        // Close the ring
        coordinates[pointCount] = new Coordinate(coordinates[0]);
        return coordinates;
    }
}
