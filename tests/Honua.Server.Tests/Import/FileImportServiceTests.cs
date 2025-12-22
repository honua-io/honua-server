// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Postgres.Features.Import;
using Xunit;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Unit tests for FileImportService format detection
/// </summary>
public class FileImportServiceTests
{
    /// <summary>
    /// Minimal mock implementation of ICrsDetectionService for unit tests
    /// </summary>
    private sealed class MockCrsDetectionService : ICrsDetectionService
    {
        public Task<int?> DetectFromPrjAsync(string prjContent) => Task.FromResult((int?)null);
        public Task<int?> DetectFromWktAsync(string wktContent) => Task.FromResult((int?)null);
        public int? DetectFromEpsgCode(string epsgCode) => null;
        public Task<int?> DetectFromGeoJsonCrsAsync(string crsObject) => Task.FromResult((int?)null);
        public Task<int?> DetectFromShapefilePrjAsync(string shapefilePath) => Task.FromResult((int?)null);
        public Task<bool> ValidateSridAsync(int srid) => Task.FromResult(true);
    }

    private static readonly ICrsDetectionService MockCrsService = new MockCrsDetectionService();

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
        var service = new FileImportService("test-connection-string", MockCrsService);

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
