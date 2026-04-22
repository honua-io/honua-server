// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Memory;

namespace Honua.Server.Tests.Infrastructure.Memory;

/// <summary>
/// Tests for optimized geometry memory management
/// </summary>
[Collection("Unit")]
public class GeometryMemoryManagerTests
{
    [Fact]
    public void RentCoordinateBuffer_ValidParameters_ReturnsUsableBuffer()
    {
        // Arrange
        var coordinateCount = 10;
        var dimensions = 2;

        // Act
        using var rental = GeometryMemoryManager.RentCoordinateBuffer(coordinateCount, dimensions);

        // Assert
        Assert.NotNull(rental.Buffer);
        Assert.Equal(coordinateCount, rental.CoordinateCount);
        Assert.Equal(dimensions, rental.Dimensions);
        Assert.Equal(coordinateCount * dimensions, rental.UsableLength);
        Assert.True(rental.Buffer.Length >= rental.UsableLength);
    }

    [Fact]
    public void CoordinateBufferRental_SetGetCoordinates_WorksCorrectly()
    {
        // Arrange
        using var rental = GeometryMemoryManager.RentCoordinateBuffer(5, 3); // 3D coordinates

        // Act
        rental.SetX(0, 10.5);
        rental.SetY(0, 20.5);
        rental.SetZ(0, 30.5);

        rental.SetX(1, 11.5);
        rental.SetY(1, 21.5);
        rental.SetZ(1, 31.5);

        // Assert
        var coord0 = rental.GetCoordinate(0);
        Assert.Equal(10.5, coord0[0], precision: 6);
        Assert.Equal(20.5, coord0[1], precision: 6);
        Assert.Equal(30.5, coord0[2], precision: 6);

        var coord1 = rental.GetCoordinate(1);
        Assert.Equal(11.5, coord1[0], precision: 6);
        Assert.Equal(21.5, coord1[1], precision: 6);
        Assert.Equal(31.5, coord1[2], precision: 6);
    }

    [Fact]
    public void CoordinateBufferRental_4DCoordinates_HandlesZAndMCorrectly()
    {
        // Arrange
        using var rental = GeometryMemoryManager.RentCoordinateBuffer(2, 4); // 4D coordinates

        // Act
        rental.SetX(0, 1.0);
        rental.SetY(0, 2.0);
        rental.SetZ(0, 3.0);
        rental.SetM(0, 4.0);

        // Assert
        var coord = rental.GetCoordinate(0);
        Assert.Equal(1.0, coord[0]);
        Assert.Equal(2.0, coord[1]);
        Assert.Equal(3.0, coord[2]);
        Assert.Equal(4.0, coord[3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public void RentCoordinateBuffer_InvalidCoordinateCount_ThrowsException(int invalidCount)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GeometryMemoryManager.RentCoordinateBuffer(invalidCount, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void RentCoordinateBuffer_InvalidDimensions_ThrowsException(int invalidDimensions)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GeometryMemoryManager.RentCoordinateBuffer(10, invalidDimensions));
    }

    [Fact]
    public void RentWkbBuffer_ValidLength_ReturnsUsableBuffer()
    {
        // Arrange
        var wkbLength = 1024;

        // Act
        using var rental = GeometryMemoryManager.RentWkbBuffer(wkbLength);

        // Assert
        Assert.NotNull(rental.Buffer);
        Assert.Equal(wkbLength, rental.UsableLength);
        Assert.True(rental.Buffer.Length >= wkbLength);
        Assert.Equal(wkbLength, rental.Memory.Length);
        Assert.Equal(wkbLength, rental.Span.Length);
    }

    [Fact]
    public void RentWkbBuffer_WithCustomBufferSize_ReturnsCorrectSize()
    {
        // Arrange
        var wkbLength = 500;
        var bufferSize = 1024;

        // Act
        using var rental = GeometryMemoryManager.RentWkbBuffer(wkbLength, bufferSize);

        // Assert
        Assert.Equal(wkbLength, rental.UsableLength);
        Assert.True(rental.Buffer.Length >= bufferSize);
    }

    [Fact]
    public void RentWkbBuffer_BufferSizeLessThanWkbLength_ThrowsException()
    {
        // Arrange
        var wkbLength = 1024;
        var bufferSize = 512;

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            GeometryMemoryManager.RentWkbBuffer(wkbLength, bufferSize));
    }

    [Fact]
    public void CoordinateBufferRental_SetZOn2D_ThrowsException()
    {
        // Arrange
        using var rental = GeometryMemoryManager.RentCoordinateBuffer(1, 2);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => rental.SetZ(0, 10.0));
    }

    [Fact]
    public void CoordinateBufferRental_SetMOnNon4D_ThrowsException()
    {
        // Arrange
        using var rental3D = GeometryMemoryManager.RentCoordinateBuffer(1, 3);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => rental3D.SetM(0, 10.0));
    }

    [Fact]
    public void CoordinateBufferRental_AccessOutOfBounds_ThrowsException()
    {
        // Arrange
        using var rental = GeometryMemoryManager.RentCoordinateBuffer(5, 2);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => rental.GetCoordinate(5));
    }

    [Fact]
    public void MultipleRentals_IndependentBuffers_DoNotInterfere()
    {
        // Arrange & Act
        using var rental1 = GeometryMemoryManager.RentCoordinateBuffer(3, 2);
        using var rental2 = GeometryMemoryManager.RentCoordinateBuffer(3, 2);

        rental1.SetX(0, 100.0);
        rental1.SetY(0, 200.0);

        rental2.SetX(0, 300.0);
        rental2.SetY(0, 400.0);

        // Assert
        var coord1 = rental1.GetCoordinate(0);
        var coord2 = rental2.GetCoordinate(0);

        Assert.Equal(100.0, coord1[0]);
        Assert.Equal(200.0, coord1[1]);
        Assert.Equal(300.0, coord2[0]);
        Assert.Equal(400.0, coord2[1]);
    }

    [Fact]
    public void ByteBufferRental_MemoryAndSpan_PointToSameData()
    {
        // Arrange
        using var rental = GeometryMemoryManager.RentWkbBuffer(10);

        // Act
        rental.Span[0] = 0xFF;
        rental.Memory.Span[1] = 0xAA;

        // Assert
        Assert.Equal(0xFF, rental.Buffer[0]);
        Assert.Equal(0xAA, rental.Buffer[1]);
        Assert.Equal(0xFF, rental.Memory.Span[0]);
        Assert.Equal(0xAA, rental.Span[1]);
    }
}
