// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Postgres.Features.Import;
using Xunit;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Unit tests for FileImportService format detection
/// </summary>
public class FileImportServiceTests
{
    [Theory]
    [InlineData("test.geojson", "GeoJson")]
    [InlineData("test.json", "GeoJson")]
    [InlineData("test.kml", "Kml")]
    [InlineData("test.txt", null)]
    [InlineData("test.xyz", null)]
    [InlineData("test", null)]
    public void DetectFormat_WithVariousExtensions_ReturnsExpectedFormat(string fileName, string? expectedFormat)
    {
        // Arrange
        var service = new FileImportService("test-connection-string");

        // Act
        var result = service.DetectFormat(fileName);

        // Assert
        if (expectedFormat == null)
        {
            result.Should().BeNull();
        }
        else
        {
            result.Should().NotBeNull();
            result.ToString().Should().Be(expectedFormat);
        }
    }
}