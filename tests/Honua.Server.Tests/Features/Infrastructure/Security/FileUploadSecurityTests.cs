// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Security;
using Honua.TestKit.Attributes;
using System.Text;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Security;

/// <summary>
/// Tests for FileUploadSecurity - critical security component for file upload validation
/// </summary>
public sealed class FileUploadSecurityTests
{
    [Fact]
    [UnitTest]
    public void DefaultMaxFileSizeBytes_HasExpectedValue()
    {
        // Assert
        FileUploadSecurity.DefaultMaxFileSizeBytes.Should().Be(100 * 1024 * 1024); // 100MB
    }

    [Fact]
    [UnitTest]
    public void MaxSecurityScanSize_HasExpectedValue()
    {
        // Assert
        FileUploadSecurity.MaxSecurityScanSize.Should().Be(10 * 1024 * 1024); // 10MB
    }

    [Fact]
    [UnitTest]
    public void ValidateFileName_ValidGeoFileName_ReturnsTrue()
    {
        // Arrange
        var validFileNames = new[]
        {
            "shapefile.shp",
            "data.geojson",
            "layer.kml",
            "points.csv",
            "raster.tiff",
            "data_2024.gpx",
            "my-layer.json",
            "file_with_underscore.shp"
        };

        // Act & Assert
        foreach (var fileName in validFileNames)
        {
            var result = FileUploadSecurity.ValidateFileName(fileName);
            result.Should().BeTrue($"'{fileName}' should be considered valid");
        }
    }

    [Theory]
    [UnitTest]
    [InlineData("file.exe")]
    [InlineData("script.bat")]
    [InlineData("virus.com")]
    [InlineData("data.scr")]
    [InlineData("malware.vbs")]
    [InlineData("../../../etc/passwd")]
    [InlineData("file\\..\\..\\windows\\system32")]
    [InlineData("CON.shp")] // Windows reserved name
    [InlineData("PRN.geojson")]
    [InlineData("AUX.kml")]
    [InlineData("file name with spaces.exe")] // Spaces + exe
    [InlineData("normalfile.shp\0hidden.exe")] // Null byte injection
    public void ValidateFileName_MaliciousFileName_ReturnsFalse(string maliciousFileName)
    {
        // Act
        var result = FileUploadSecurity.ValidateFileName(maliciousFileName);

        // Assert
        result.Should().BeFalse($"'{maliciousFileName}' should be considered malicious");
    }

    [Fact]
    [UnitTest]
    public void ValidateFileContent_ValidGeoJSONContent_ReturnsTrue()
    {
        // Arrange
        var validGeoJSON = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "geometry": {
                "type": "Point",
                "coordinates": [-122.4194, 37.7749]
              },
              "properties": {
                "name": "San Francisco"
              }
            }
          ]
        }
        """;
        var content = Encoding.UTF8.GetBytes(validGeoJSON);

        // Act
        var result = FileUploadSecurity.ValidateFileContent(content, "application/json");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    [UnitTest]
    public void ValidateFileContent_PEExecutableSignature_ReturnsFalse()
    {
        // Arrange - PE executable signature (MZ header)
        var maliciousContent = new byte[] { 0x4D, 0x5A, 0x90, 0x00 }; // MZ header + padding

        // Act
        var result = FileUploadSecurity.ValidateFileContent(maliciousContent, "application/octet-stream");

        // Assert
        result.Should().BeFalse("PE executable content should be rejected");
    }

    [Fact]
    [UnitTest]
    public void ValidateFileContent_ELFExecutableSignature_ReturnsFalse()
    {
        // Arrange - ELF executable signature
        var maliciousContent = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01 }; // ELF header

        // Act
        var result = FileUploadSecurity.ValidateFileContent(maliciousContent, "application/octet-stream");

        // Assert
        result.Should().BeFalse("ELF executable content should be rejected");
    }

    [Fact]
    [UnitTest]
    public void ValidateFileContent_ShellScriptSignature_ReturnsFalse()
    {
        // Arrange - Shell script with shebang
        var scriptContent = Encoding.ASCII.GetBytes("#!/bin/bash\nrm -rf /");

        // Act
        var result = FileUploadSecurity.ValidateFileContent(scriptContent, "text/plain");

        // Assert
        result.Should().BeFalse("Shell script content should be rejected");
    }

    [Fact]
    [UnitTest]
    public void ValidateFileContent_BatchScriptSignature_ReturnsFalse()
    {
        // Arrange - Windows batch script
        var batchContent = Encoding.ASCII.GetBytes("@echo off\ndel /f /q C:\\*");

        // Act
        var result = FileUploadSecurity.ValidateFileContent(batchContent, "text/plain");

        // Assert
        result.Should().BeFalse("Batch script content should be rejected");
    }

    [Theory]
    [UnitTest]
    [InlineData("application/octet-stream")]
    [InlineData("application/zip")]
    [InlineData("application/json")]
    [InlineData("text/csv")]
    [InlineData("application/vnd.google-earth.kml+xml")]
    [InlineData("image/tiff")]
    public void ValidateMimeType_AllowedMimeType_ReturnsTrue(string allowedMimeType)
    {
        // Act
        var result = FileUploadSecurity.ValidateMimeType(allowedMimeType);

        // Assert
        result.Should().BeTrue($"'{allowedMimeType}' should be allowed");
    }

    [Theory]
    [UnitTest]
    [InlineData("application/x-msdownload")] // Executable
    [InlineData("application/x-ms-dos-executable")]
    [InlineData("application/x-executable")]
    [InlineData("application/x-shockwave-flash")]
    [InlineData("text/javascript")]
    [InlineData("application/javascript")]
    [InlineData("text/vbscript")]
    [InlineData("application/x-python-code")]
    public void ValidateMimeType_DisallowedMimeType_ReturnsFalse(string disallowedMimeType)
    {
        // Act
        var result = FileUploadSecurity.ValidateMimeType(disallowedMimeType);

        // Assert
        result.Should().BeFalse($"'{disallowedMimeType}' should be disallowed");
    }

    [Theory]
    [UnitTest]
    [InlineData(1024)] // 1KB
    [InlineData(1024 * 1024)] // 1MB
    [InlineData(50 * 1024 * 1024)] // 50MB
    [InlineData(FileUploadSecurity.DefaultMaxFileSizeBytes)] // Exactly at limit
    public void ValidateFileSize_WithinLimits_ReturnsTrue(long validSize)
    {
        // Act
        var result = FileUploadSecurity.ValidateFileSize(validSize);

        // Assert
        result.Should().BeTrue($"Size {validSize} should be within limits");
    }

    [Theory]
    [UnitTest]
    [InlineData(FileUploadSecurity.DefaultMaxFileSizeBytes + 1)] // Just over limit
    [InlineData(200 * 1024 * 1024)] // 200MB
    [InlineData(long.MaxValue)]
    [InlineData(-1)] // Negative size
    [InlineData(0)] // Zero size
    public void ValidateFileSize_ExceedsLimits_ReturnsFalse(long invalidSize)
    {
        // Act
        var result = FileUploadSecurity.ValidateFileSize(invalidSize);

        // Assert
        result.Should().BeFalse($"Size {invalidSize} should exceed limits");
    }

    [Fact]
    [UnitTest]
    public void ValidateFileSize_WithCustomMaxSize_RespectsCustomLimit()
    {
        // Arrange
        const long customMaxSize = 5 * 1024 * 1024; // 5MB
        const long testSize = 6 * 1024 * 1024; // 6MB

        // Act
        var result = FileUploadSecurity.ValidateFileSize(testSize, customMaxSize);

        // Assert
        result.Should().BeFalse("Size should exceed custom limit");
    }

    [Fact]
    [UnitTest]
    public void SanitizeFileName_MaliciousInput_ReturnsSanitized()
    {
        // Arrange
        var maliciousFileName = "../../../etc/passwd";

        // Act
        var result = FileUploadSecurity.SanitizeFileName(maliciousFileName);

        // Assert
        result.Should().NotContain("..");
        result.Should().NotContain("/");
        result.Should().NotContain("\\");
        result.Should().NotBeEmpty();
    }

    [Theory]
    [UnitTest]
    [InlineData("normal_file.shp", "normal_file.shp")]
    [InlineData("file with spaces.geojson", "file_with_spaces.geojson")]
    [InlineData("file-with-dashes.kml", "file-with-dashes.kml")]
    [InlineData("file.with.dots.csv", "file.with.dots.csv")]
    public void SanitizeFileName_ValidInput_ReturnsExpectedOutput(string input, string expected)
    {
        // Act
        var result = FileUploadSecurity.SanitizeFileName(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    [UnitTest]
    public void GetFileExtension_ValidFileName_ReturnsCorrectExtension()
    {
        // Arrange
        var testCases = new[]
        {
            ("file.shp", ".shp"),
            ("data.geojson", ".geojson"),
            ("layer.KML", ".kml"), // Should be lowercase
            ("noextension", ""),
            ("file.with.multiple.dots.csv", ".csv")
        };

        // Act & Assert
        foreach (var (fileName, expectedExtension) in testCases)
        {
            var result = FileUploadSecurity.GetFileExtension(fileName);
            result.Should().Be(expectedExtension, $"'{fileName}' should have extension '{expectedExtension}'");
        }
    }

    [Fact]
    [UnitTest]
    public void ComprehensiveValidation_ValidGeoFile_PassesAllChecks()
    {
        // Arrange
        const string fileName = "valid_layer.geojson";
        const string mimeType = "application/json";
        const long fileSize = 1024 * 1024; // 1MB
        var content = Encoding.UTF8.GetBytes("""{"type": "FeatureCollection", "features": []}""");

        // Act & Assert
        FileUploadSecurity.ValidateFileName(fileName).Should().BeTrue();
        FileUploadSecurity.ValidateMimeType(mimeType).Should().BeTrue();
        FileUploadSecurity.ValidateFileSize(fileSize).Should().BeTrue();
        FileUploadSecurity.ValidateFileContent(content, mimeType).Should().BeTrue();
    }

    [Fact]
    [UnitTest]
    public void ComprehensiveValidation_MaliciousFile_FailsSecurityChecks()
    {
        // Arrange - Malicious file disguised as geospatial data
        const string fileName = "malware.exe";
        const string mimeType = "application/x-msdownload";
        const long fileSize = 200 * 1024 * 1024; // 200MB - too large
        var content = new byte[] { 0x4D, 0x5A, 0x90, 0x00 }; // PE executable signature

        // Act & Assert
        FileUploadSecurity.ValidateFileName(fileName).Should().BeFalse();
        FileUploadSecurity.ValidateMimeType(mimeType).Should().BeFalse();
        FileUploadSecurity.ValidateFileSize(fileSize).Should().BeFalse();
        FileUploadSecurity.ValidateFileContent(content, "application/octet-stream").Should().BeFalse();
    }
}