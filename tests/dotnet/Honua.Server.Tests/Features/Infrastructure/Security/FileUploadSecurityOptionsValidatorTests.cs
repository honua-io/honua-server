// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Security;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Security;

/// <summary>
/// Unit tests for FileUploadSecurityOptionsValidator to ensure proper validation of security configuration.
/// </summary>
public class FileUploadSecurityOptionsValidatorTests
{
    private readonly FileUploadSecurityOptionsValidator _validator = new();

    [UnitTest]
    public void Validate_ValidConfiguration_ReturnsSuccess()
    {
        // Arrange
        var options = new FileUploadSecurityOptions
        {
            MaxSecurityScanSizeBytes = 10 * 1024 * 1024 // 10MB
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures ?? Array.Empty<string>());
    }

    [UnitTest]
    public void Validate_NegativeMaxSecurityScanSize_ReturnsFail()
    {
        // Arrange
        var options = new FileUploadSecurityOptions
        {
            MaxSecurityScanSizeBytes = -1024 // Invalid: negative
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxSecurityScanSizeBytes") && f.Contains("must be between"));
    }

    [UnitTest]
    public void Validate_ZeroMaxSecurityScanSize_ReturnsFail()
    {
        // Arrange
        var options = new FileUploadSecurityOptions
        {
            MaxSecurityScanSizeBytes = 0 // Invalid: zero
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxSecurityScanSizeBytes") && f.Contains("must be between"));
    }

    [UnitTest]
    public void Validate_MaxSecurityScanSizeTooSmall_ReturnsFail()
    {
        // Arrange
        var options = new FileUploadSecurityOptions
        {
            MaxSecurityScanSizeBytes = 512 // Invalid: too small (512 bytes)
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxSecurityScanSizeBytes") && f.Contains("must be between 1KB and 100MB"));
    }

    [UnitTest]
    public void Validate_MaxSecurityScanSizeTooLarge_ReturnsFail()
    {
        // Arrange
        var options = new FileUploadSecurityOptions
        {
            MaxSecurityScanSizeBytes = 200 * 1024 * 1024 // Invalid: 200MB
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxSecurityScanSizeBytes") && f.Contains("must be between 1KB and 100MB"));
    }

    [UnitTest]
    public void Validate_MaxSecurityScanSizeSignificantlyLargerThanDefault_ReturnsFail()
    {
        // Arrange
        var options = new FileUploadSecurityOptions
        {
            MaxSecurityScanSizeBytes = FileUploadSecurity.MaxSecurityScanSize * 3 // Significantly larger than default
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxSecurityScanSizeBytes") && f.Contains("significantly larger than recommended default"));
    }

    [UnitTest]
    public void Validate_MaxSecurityScanSizeBelowRecommendedMinimum_ReturnsFail()
    {
        // Arrange
        var options = new FileUploadSecurityOptions
        {
            MaxSecurityScanSizeBytes = 256 * 1024 // 256KB - below recommended minimum
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxSecurityScanSizeBytes") && f.Contains("below recommended minimum"));
    }

    [UnitTest]
    public void Validate_MaxSecurityScanSizeAboveRecommendedMaximum_ReturnsFail()
    {
        // Arrange
        var options = new FileUploadSecurityOptions
        {
            MaxSecurityScanSizeBytes = 75 * 1024 * 1024 // 75MB - above recommended maximum
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxSecurityScanSizeBytes") && f.Contains("exceeds recommended maximum"));
    }

    [UnitTest]
    public void Validate_NullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _validator.Validate(null, null!));
    }

    [UnitTest]
    public void Validate_RecommendedSizes_ReturnsSuccess()
    {
        // Arrange & Act & Assert
        var validSizes = new[]
        {
            1024 * 1024,       // 1MB
            5 * 1024 * 1024,   // 5MB
            10 * 1024 * 1024,  // 10MB (default)
            20 * 1024 * 1024   // 20MB (2x default max)
        };

        foreach (var size in validSizes)
        {
            var options = new FileUploadSecurityOptions
            {
                MaxSecurityScanSizeBytes = size
            };

            var result = _validator.Validate(null, options);

            Assert.True(result.Succeeded, $"Size {size:N0} bytes should be valid");
        }
    }

    [UnitTest]
    public void Validate_BoundaryValues_ReturnsAppropriateResults()
    {
        // Test minimum boundary
        var minOptions = new FileUploadSecurityOptions
        {
            MaxSecurityScanSizeBytes = 1024 // Exact minimum
        };
        var minResult = _validator.Validate(null, minOptions);
        Assert.False(minResult.Succeeded); // Should fail due to being below recommended minimum

        // Test maximum boundary
        var maxOptions = new FileUploadSecurityOptions
        {
            MaxSecurityScanSizeBytes = 100 * 1024 * 1024 // Exact maximum
        };
        var maxResult = _validator.Validate(null, maxOptions);
        Assert.False(maxResult.Succeeded); // Should fail due to recommended maximum limits
    }
}
