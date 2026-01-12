// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Configuration;

/// <summary>
/// Unit tests for LimitsOptionsValidator to ensure proper validation of configuration limits.
/// </summary>
public class LimitsOptionsValidatorTests
{
    private readonly LimitsOptionsValidator _validator = new();

    [UnitTest]
    public void Validate_ValidConfiguration_ReturnsSuccess()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Query = new QueryLimits
            {
                MaxRecordCount = 2000,
                DefaultRecordCount = 1000,
                MaxOffset = 100000,
                MaxBboxAreaSqKm = 500,
                QueryTimeout = TimeSpan.FromSeconds(30)
            },
            Geometry = new GeometryLimits
            {
                MaxVerticesPerGeometry = 50000,
                MaxGeometrySize = 5242880, // 5MB
                MaxCoordinatePrecision = 8,
                SimplifyTolerance = null
            },
            Edits = new EditLimits
            {
                MaxFeaturesPerEdit = 500,
                MaxEditsPerTransaction = 2500,
                MaxPayloadSize = 26214400 // 25MB
            },
            Attachments = new AttachmentLimits
            {
                MaxAttachmentSize = 5242880, // 5MB
                MaxAttachmentsPerFeature = 5,
                MaxTotalAttachmentSize = 52428800, // 50MB
                AllowedMimeTypes = "image/*,application/pdf"
            },
            Tiles = new TileLimits
            {
                MaxTileZoom = 18,
                MinTileZoom = 0,
                MaxFeaturesPerTile = 50000,
                TileTimeout = TimeSpan.FromSeconds(5),
                MaxTileSize = 256000
            },
            Connections = new ConnectionLimits
            {
                MaxConcurrentQueries = 50,
                MaxConnectionPoolSize = 50,
                RequestTimeout = TimeSpan.FromSeconds(60)
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures ?? Array.Empty<string>());
    }

    [UnitTest]
    public void Validate_DefaultRecordCountExceedsMaxRecordCount_ReturnsFail()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Query = new QueryLimits
            {
                MaxRecordCount = 1000,
                DefaultRecordCount = 2000 // Invalid: exceeds max
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Query.DefaultRecordCount") && f.Contains("must not exceed Query.MaxRecordCount"));
    }

    [UnitTest]
    public void Validate_MinTileZoomExceedsMaxTileZoom_ReturnsFail()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Tiles = new TileLimits
            {
                MinTileZoom = 10,
                MaxTileZoom = 5 // Invalid: min exceeds max
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Tiles.MinTileZoom") && f.Contains("must not exceed Tiles.MaxTileZoom"));
    }

    [UnitTest]
    public void Validate_AttachmentSizeLimitsInconsistent_ReturnsFail()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Attachments = new AttachmentLimits
            {
                MaxAttachmentSize = 10485760, // 10MB
                MaxAttachmentsPerFeature = 10,
                MaxTotalAttachmentSize = 52428800, // 50MB - but 10MB * 10 = 100MB exceeds this
                AllowedMimeTypes = "image/*"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxAttachmentSize") && f.Contains("exceeds MaxTotalAttachmentSize"));
    }

    [UnitTest]
    public void Validate_EmptyAllowedMimeTypes_ReturnsFail()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Attachments = new AttachmentLimits
            {
                AllowedMimeTypes = "" // Invalid: empty
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("AllowedMimeTypes") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_InvalidMimeType_ReturnsFail()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Attachments = new AttachmentLimits
            {
                AllowedMimeTypes = "image/*,invalid-mime-type,application/pdf" // Invalid MIME type
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("invalid MIME type"));
    }

    [UnitTest]
    public void Validate_QueryTimeoutOutOfRange_ReturnsFail()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Query = new QueryLimits
            {
                QueryTimeout = TimeSpan.FromSeconds(2) // Too short
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("QueryTimeout") && f.Contains("between 5 seconds and 2 minutes"));
    }

    [UnitTest]
    public void Validate_DataAnnotationViolations_ReturnsFail()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Query = new QueryLimits
            {
                MaxRecordCount = 50, // Below minimum of 100
                DefaultRecordCount = 50 // Below minimum of 100
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxRecordCount") && f.Contains("between 100 and 10,000"));
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("DefaultRecordCount") && f.Contains("at least 100"));
    }

    [UnitTest]
    public void Validate_NullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _validator.Validate(null, null!));
    }

    [UnitTest]
    public void Validate_ValidMimeTypes_ReturnsSuccess()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Attachments = new AttachmentLimits
            {
                AllowedMimeTypes = "image/png,image/jpeg,image/*,application/pdf,text/plain"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [UnitTest]
    public void Validate_NegativeBboxArea_ReturnsFail()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Query = new QueryLimits
            {
                MaxBboxAreaSqKm = -100 // Invalid: negative
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Query.MaxBboxAreaSqKm") && f.Contains("must be between 0.1"));
    }

    [UnitTest]
    public void Validate_RequestTimeoutOutOfRange_ReturnsFail()
    {
        // Arrange
        var options = new LimitsOptions
        {
            Connections = new ConnectionLimits
            {
                RequestTimeout = TimeSpan.FromSeconds(5) // Too short
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("RequestTimeout") && f.Contains("between 10 seconds and 10 minutes"));
    }
}
