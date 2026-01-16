// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// Tests for file upload security validation.
/// Validates path traversal prevention, malicious file detection, and content validation.
/// </summary>
[Protocol(Protocols.FeatureServer)]
public sealed class FileUploadSecurityTests
{
    #region File Name Validation

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateFileName_WithPathTraversal_RejectsFile()
    {
        // Arrange - path traversal attempts
        var dangerousNames = new[]
        {
            "../../../etc/passwd",
            "..\\..\\windows\\system32\\config",
            "file/../../../secret.txt",
            "normal/../../attack.exe",
            "....//....//etc/hosts"
        };

        foreach (var name in dangerousNames)
        {
            // Act
            var result = FileUploadSecurity.ValidateFileName(name);

            // Assert
            result.IsValid.Should().BeFalse($"'{name}' should be rejected as path traversal");
            result.ErrorMessage.Should().Contain("invalid", $"'{name}' error should mention invalid");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateFileName_WithNullBytes_RejectsFile()
    {
        // Arrange - null byte injection
        var nullByteNames = new[]
        {
            "file.txt\0.exe",
            "image\0.jpg",
            "document.pdf\0malicious.exe"
        };

        foreach (var name in nullByteNames)
        {
            // Act
            var result = FileUploadSecurity.ValidateFileName(name);

            // Assert
            result.IsValid.Should().BeFalse($"'{name}' with null byte should be rejected");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateFileName_WithDangerousExtensions_RejectsFile()
    {
        // Arrange - executable extensions
        var dangerousNames = new[]
        {
            "malware.exe",
            "script.bat",
            "virus.com",
            "payload.cmd",
            "attack.vbs",
            "backdoor.js"
        };

        foreach (var name in dangerousNames)
        {
            // Act
            var result = FileUploadSecurity.ValidateFileName(name);

            // Assert
            result.IsValid.Should().BeFalse($"'{name}' should be rejected as dangerous extension");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateFileName_WithSafeNames_AcceptsFile()
    {
        // Arrange - safe file names
        var safeNames = new[]
        {
            "document.pdf",
            "image.jpg",
            "data.geojson",
            "shapefile.shp",
            "layer.gpkg",
            @"C:\fakepath\layer.gpkg"
        };

        foreach (var name in safeNames)
        {
            // Act
            var result = FileUploadSecurity.ValidateFileName(name);

            // Assert
            result.IsValid.Should().BeTrue($"'{name}' should be accepted");
        }
    }

    #endregion

    #region File Name Sanitization

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void SanitizeFileName_RemovesPathComponents()
    {
        // Arrange
        var input = "/path/to/../../file.txt";

        // Act
        var result = FileUploadSecurity.SanitizeFileName(input);

        // Assert
        result.Should().NotContain("..");
        result.Should().NotContain("/");
        result.Should().NotContain("\\");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void SanitizeFileName_RemovesDangerousCharacters()
    {
        // Arrange
        var input = "file<>:\"|?*.txt";

        // Act
        var result = FileUploadSecurity.SanitizeFileName(input);

        // Assert
        result.Should().NotContain("<");
        result.Should().NotContain(">");
        result.Should().NotContain(":");
        result.Should().NotContain("\"");
        result.Should().NotContain("|");
        result.Should().NotContain("?");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void SanitizeFileName_TruncatesLongNames()
    {
        // Arrange
        var longName = new string('a', 300) + ".txt";

        // Act
        var result = FileUploadSecurity.SanitizeFileName(longName);

        // Assert
        result.Length.Should().BeLessOrEqualTo(204); // 200 + extension
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void SanitizeFileName_HandlesEmptyInput()
    {
        // Act
        var result1 = FileUploadSecurity.SanitizeFileName("");
        var result2 = FileUploadSecurity.SanitizeFileName("   ");
        var result3 = FileUploadSecurity.SanitizeFileName(null!);

        // Assert
        result1.Should().NotBeNullOrEmpty();
        result2.Should().NotBeNullOrEmpty();
        result3.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region MIME Type Validation

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateMimeType_WithAllowedTypes_Succeeds()
    {
        // Arrange - allowed MIME types for geospatial data
        var allowedTypes = new[]
        {
            "application/geo+json",
            "application/json",
            "text/csv",
            "application/zip",
            "application/xml"
        };

        foreach (var mimeType in allowedTypes)
        {
            // Act
            var result = FileUploadSecurity.ValidateMimeType(mimeType);

            // Assert
            result.IsValid.Should().BeTrue($"'{mimeType}' should be allowed");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateMimeType_WithDisallowedTypes_Fails()
    {
        // Arrange - potentially dangerous MIME types
        var disallowedTypes = new[]
        {
            "application/x-executable",
            "application/x-msdownload",
            "application/x-php",
            "text/javascript"
        };

        foreach (var mimeType in disallowedTypes)
        {
            // Act
            var result = FileUploadSecurity.ValidateMimeType(mimeType);

            // Assert
            result.IsValid.Should().BeFalse($"'{mimeType}' should be rejected");
        }
    }

    #endregion

    #region File Size Validation

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateFileSize_WithZeroSize_Fails()
    {
        // Act
        var result = FileUploadSecurity.ValidateFileSize(0);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("greater than zero");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateFileSize_WithNegativeSize_Fails()
    {
        // Act
        var result = FileUploadSecurity.ValidateFileSize(-1);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateFileSize_WithOversizedFile_Fails()
    {
        // Arrange - 200MB file (over 100MB limit)
        var oversizedBytes = 200L * 1024 * 1024;

        // Act
        var result = FileUploadSecurity.ValidateFileSize(oversizedBytes);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exceeds maximum");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateFileSize_WithValidSize_Succeeds()
    {
        // Arrange - 5MB file
        var validBytes = 5L * 1024 * 1024;

        // Act
        var result = FileUploadSecurity.ValidateFileSize(validBytes);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    #endregion

    #region Content Validation

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public async Task ValidateFileContentAsync_WithScanLimit_IgnoresContentBeyondLimit()
    {
        // Arrange
        var content = new string('a', 2000) + "<script>alert('x')</script>";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var file = CreateFormFile(stream, "data.txt", "text/plain");

        // Act
        var result = await FileUploadSecurity.ValidateFileContentAsync(file, maxScanSizeBytes: 1024);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public async Task ValidateFileContentAsync_WithLargerScanLimit_DetectsContent()
    {
        // Arrange
        var content = new string('a', 2000) + "<script>alert('x')</script>";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var file = CreateFormFile(stream, "data.txt", "text/plain");

        // Act
        var result = await FileUploadSecurity.ValidateFileContentAsync(file, maxScanSizeBytes: 4096);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void FileUploadSecurityOptions_BindsFromEnvironmentVariables()
    {
        const string envKey = "FileUploadSecurity__MaxSecurityScanSizeBytes";
        var previousValue = Environment.GetEnvironmentVariable(envKey);

        try
        {
            Environment.SetEnvironmentVariable(envKey, "12345");

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();

            var options = new FileUploadSecurityOptions();
            configuration.GetSection(FileUploadSecurityOptions.SectionName).Bind(options);

            options.MaxSecurityScanSizeBytes.Should().Be(12345);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, previousValue);
        }
    }

    #endregion

    #region Extension Validation

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateFileExtension_WithGeospatialExtensions_Succeeds()
    {
        // Arrange
        var geospatialFiles = new[]
        {
            "data.shp",
            "data.geojson",
            "data.gpkg",
            "data.kml",
            "data.csv"
        };

        foreach (var filename in geospatialFiles)
        {
            // Act
            var result = FileUploadSecurity.ValidateFileExtension(filename);

            // Assert
            result.IsValid.Should().BeTrue($"'{filename}' should have allowed extension");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addAttachment")]
    public void ValidateFileExtension_WithNoExtension_Fails()
    {
        // Act
        var result = FileUploadSecurity.ValidateFileExtension("filenoext");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("extension");
    }

    #endregion

    private static FormFile CreateFormFile(Stream stream, string fileName, string contentType)
    {
        var file = new FormFile(stream, 0, stream.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };

        return file;
    }
}
