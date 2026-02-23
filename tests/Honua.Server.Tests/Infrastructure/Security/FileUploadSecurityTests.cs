// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Security;

/// <summary>
/// Unit tests for file upload security validation.
/// </summary>
public class FileUploadSecurityTests
{
    private readonly ITestOutputHelper _output;

    public FileUploadSecurityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("test.shp", true)]
    [InlineData("data.csv", true)]
    [InlineData("layer.geojson", true)]
    [InlineData("archive.zip", true)]
    [InlineData("malware.exe", false)]
    [InlineData("script.bat", false)]
    [InlineData("virus.scr", false)]
    [InlineData("trojan.com", false)]
    [SecurityTest]
    public void ValidateFileName_VariousExtensions_ReturnsExpectedResult(string fileName, bool shouldBeValid)
    {
        // Act
        var result = FileUploadSecurity.ValidateFileName(fileName);

        // Assert
        Assert.Equal(shouldBeValid, result.IsValid);
        if (!shouldBeValid)
        {
            Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        }

        _output.WriteLine($"File '{fileName}': {(result.IsValid ? "Valid" : $"Invalid - {result.ErrorMessage}")}");
    }

    [Theory]
    [InlineData("../../etc/passwd", false)]
    [InlineData("../config.txt", false)]
    [InlineData("folder/file.csv", false)]
    [InlineData("C:\\Windows\\system32\\evil.exe", false)]
    [InlineData("normal_file.shp", true)]
    [SecurityTest]
    public void ValidateFileName_PathTraversal_RejectsUnsafeNames(string fileName, bool shouldBeValid)
    {
        // Act
        var result = FileUploadSecurity.ValidateFileName(fileName);

        // Assert
        Assert.Equal(shouldBeValid, result.IsValid);
        if (!shouldBeValid)
        {
            Assert.Contains("invalid path characters", result.ErrorMessage);
        }

        _output.WriteLine($"Path traversal test '{fileName}': {(result.IsValid ? "Valid" : $"Invalid - {result.ErrorMessage}")}");
    }

    [Theory]
    [InlineData("C:\\fakepath\\layer.gpkg")]
    [InlineData("C:/fakepath/layer.gpkg")]
    [SecurityTest]
    public void ValidateFileName_BrowserFakePath_AcceptsFile(string fileName)
    {
        // Act
        var result = FileUploadSecurity.ValidateFileName(fileName);

        // Assert
        Assert.True(result.IsValid);

        _output.WriteLine($"Fake path test '{fileName}': Valid");
    }

    [Theory]
    [InlineData(".csv", true)]
    [InlineData(".geojson", true)]
    [InlineData(".shp", true)]
    [InlineData(".exe", false)]
    [InlineData(".js", false)]
    [InlineData(".vbs", false)]
    [SecurityTest]
    public void ValidateFileExtension_VariousExtensions_ReturnsExpectedResult(string extension, bool shouldBeValid)
    {
        // Arrange
        var fileName = "test" + extension;

        // Act
        var result = FileUploadSecurity.ValidateFileExtension(fileName);

        // Assert
        Assert.Equal(shouldBeValid, result.IsValid);

        _output.WriteLine($"Extension '{extension}': {(result.IsValid ? "Allowed" : "Blocked")}");
    }

    [Theory]
    [InlineData("text/csv", true)]
    [InlineData("application/geo+json", true)]
    [InlineData("application/zip", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData("application/x-executable", false)]
    [InlineData("text/html", false)]
    [InlineData("application/javascript", false)]
    [SecurityTest]
    public void ValidateMimeType_VariousTypes_ReturnsExpectedResult(string mimeType, bool shouldBeValid)
    {
        // Act
        var result = FileUploadSecurity.ValidateMimeType(mimeType);

        // Assert
        Assert.Equal(shouldBeValid, result.IsValid);

        _output.WriteLine($"MIME type '{mimeType}': {(result.IsValid ? "Allowed" : "Blocked")}");
    }

    [Theory]
    [InlineData(1024, true)]              // 1KB
    [InlineData(1024 * 1024, true)]       // 1MB
    [InlineData(50 * 1024 * 1024, true)]  // 50MB
    [InlineData(101 * 1024 * 1024, false)] // 101MB (over limit)
    [InlineData(0, false)]                 // Empty file
    [InlineData(-1, false)]                // Invalid size
    [SecurityTest]
    public void ValidateFileSize_VariousSizes_ReturnsExpectedResult(long fileSize, bool shouldBeValid)
    {
        // Act
        var result = FileUploadSecurity.ValidateFileSize(fileSize);

        // Assert
        Assert.Equal(shouldBeValid, result.IsValid);

        _output.WriteLine($"File size {fileSize:N0} bytes: {(result.IsValid ? "Allowed" : "Blocked")}");
    }

    [Fact]
    [SecurityTest]
    public async Task ValidateFileContent_MaliciousExecutable_RejectsFile()
    {
        // Arrange - Create a mock file with PE header (executable signature)
        var maliciousContent = new byte[] { 0x4D, 0x5A, 0x90, 0x00 }; // PE header
        var mockFile = CreateMockFormFile("malware.csv", "text/csv", maliciousContent);

        // Act
        var result = await FileUploadSecurity.ValidateFileAsync(mockFile);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("malicious signature", result.ErrorMessage);

        _output.WriteLine($"Malicious content detection: {result.ErrorMessage}");
    }

    [Fact]
    [SecurityTest]
    public async Task ValidateFileContent_ScriptContent_RejectsFile()
    {
        // Arrange - Create a mock CSV file with script content
        var scriptContent = System.Text.Encoding.UTF8.GetBytes("Name,Value\n<script>alert('xss')</script>,123");
        var mockFile = CreateMockFormFile("data.csv", "text/csv", scriptContent);

        // Act
        var result = await FileUploadSecurity.ValidateFileAsync(mockFile);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("dangerous script content", result.ErrorMessage);

        _output.WriteLine($"Script content detection: {result.ErrorMessage}");
    }

    [Fact]
    [SecurityTest]
    public async Task ValidateFileContent_LegitimateCSV_AcceptsFile()
    {
        // Arrange - Create a legitimate CSV file
        var csvContent = System.Text.Encoding.UTF8.GetBytes("Name,Latitude,Longitude\nPoint A,40.7128,-74.0060\nPoint B,34.0522,-118.2437");
        var mockFile = CreateMockFormFile("data.csv", "text/csv", csvContent);

        // Act
        var result = await FileUploadSecurity.ValidateFileAsync(mockFile);

        // Assert
        Assert.True(result.IsValid);

        _output.WriteLine("Legitimate CSV file validated successfully");
    }

    [Theory]
    [InlineData("file<script>.csv")]
    [InlineData("data\x00file.csv")]
    [InlineData("test\x1f.csv")]
    [InlineData("file\"name.csv")]
    [SecurityTest]
    public void ValidateFileName_DangerousCharacters_RejectsFile(string fileName)
    {
        // Act
        var result = FileUploadSecurity.ValidateFileName(fileName);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("invalid characters", result.ErrorMessage);

        _output.WriteLine($"Dangerous character test '{fileName}': Rejected - {result.ErrorMessage}");
    }

    [Fact]
    [SecurityTest]
    public void SanitizeFileName_VeryLongExtension_DoesNotThrow()
    {
        // Arrange - extension longer than 200 chars
        var longExtension = "." + new string('x', 250);
        var fileName = "file" + longExtension;

        // Act - should not throw ArgumentOutOfRangeException
        var sanitizedName = FileUploadSecurity.SanitizeFileName(fileName);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(sanitizedName));
        _output.WriteLine($"Sanitized long extension: '{sanitizedName}' (length={sanitizedName.Length})");
    }

    [Fact]
    [SecurityTest]
    public void SanitizeFileName_DangerousName_ReturnsSafeName()
    {
        // Arrange
        var dangerousName = "../../system<>file\x00.csv";

        // Act
        var sanitizedName = FileUploadSecurity.SanitizeFileName(dangerousName);

        // Assert
        Assert.DoesNotContain("..", sanitizedName);
        Assert.DoesNotContain("<", sanitizedName);
        Assert.DoesNotContain(">", sanitizedName);
        Assert.DoesNotContain(sanitizedName, c => char.IsControl(c));
        Assert.False(string.IsNullOrWhiteSpace(sanitizedName));

        _output.WriteLine($"Sanitized '{dangerousName}' to '{sanitizedName}'");
    }

    private static FormFile CreateMockFormFile(string fileName, string contentType, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }
}
