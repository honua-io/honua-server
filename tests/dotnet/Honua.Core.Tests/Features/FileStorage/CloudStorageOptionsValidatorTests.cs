// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FileStorage;

/// <summary>
/// Unit tests for CloudStorageOptionsValidator to ensure proper validation of cloud storage configuration.
/// </summary>
public class CloudStorageOptionsValidatorTests
{
    private readonly CloudStorageOptionsValidator _validator = new();

    [UnitTest]
    public void Validate_ValidLocalStorageConfiguration_ReturnsSuccess()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.Local,
            DefaultTimeToLive = TimeSpan.FromHours(24),
            MaxFileSizeBytes = 100 * 1024 * 1024, // 100MB
            LocalStorage = new LocalStorageOptions
            {
                BasePath = "/tmp/honua-storage",
                CreateDirectoryIfNotExists = true
            },
            EnableAutomaticCleanup = true,
            CleanupInterval = TimeSpan.FromHours(1)
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures ?? Array.Empty<string>());
    }

    [UnitTest]
    public void Validate_ValidAwsS3Configuration_ReturnsSuccess()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            DefaultTimeToLive = TimeSpan.FromHours(24),
            MaxFileSizeBytes = 100 * 1024 * 1024,
            AwsS3 = new AwsS3Options
            {
                BucketName = "my-bucket",
                Region = "us-east-1",
                KeyPrefix = "uploads/",
                AccessKeyId = "AKIAIOSFODNN7EXAMPLE",
                SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
                EnableServerSideEncryption = true
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures ?? Array.Empty<string>());
    }

    [UnitTest]
    public void Validate_InvalidTimeToLive_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.Local,
            DefaultTimeToLive = TimeSpan.FromSeconds(-100), // Invalid: negative
            LocalStorage = new LocalStorageOptions
            {
                BasePath = "/tmp/storage"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("DefaultTimeToLive", StringComparison.Ordinal) && f.Contains("between 60 seconds", StringComparison.Ordinal));
    }

    [UnitTest]
    public void Validate_TimeToLiveTooLong_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.Local,
            DefaultTimeToLive = TimeSpan.FromDays(400), // Invalid: too long
            LocalStorage = new LocalStorageOptions
            {
                BasePath = "/tmp/storage"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("DefaultTimeToLive", StringComparison.Ordinal) && f.Contains("between 60 seconds", StringComparison.Ordinal));
    }

    [UnitTest]
    public void Validate_MaxFileSizeTooSmall_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.Local,
            MaxFileSizeBytes = 512 * 1024, // Invalid: too small (512KB)
            LocalStorage = new LocalStorageOptions
            {
                BasePath = "/tmp/storage"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxFileSizeBytes", StringComparison.Ordinal) && f.Contains("between 1.0MB and 10.0GB", StringComparison.Ordinal));
    }

    [UnitTest]
    public void Validate_MaxFileSizeTooLarge_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.Local,
            MaxFileSizeBytes = 20L * 1024L * 1024L * 1024L, // Invalid: 20GB
            LocalStorage = new LocalStorageOptions
            {
                BasePath = "/tmp/storage"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("MaxFileSizeBytes", StringComparison.Ordinal) && f.Contains("between 1.0MB and 10.0GB", StringComparison.Ordinal));
    }

    [UnitTest]
    public void Validate_LocalStorageMissingBasePath_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.Local,
            LocalStorage = new LocalStorageOptions
            {
                BasePath = "" // Invalid: empty
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("BasePath") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_LocalStorageDirectoryTraversal_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.Local,
            LocalStorage = new LocalStorageOptions
            {
                BasePath = "/tmp/../../../etc/passwd" // Invalid: directory traversal
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("LocalStorage.BasePath", StringComparison.Ordinal) && f.Contains("path traversal", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void Validate_LocalStorageRelativePath_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.Local,
            LocalStorage = new LocalStorageOptions
            {
                BasePath = "relative/path" // Invalid: relative path
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("LocalStorage.BasePath", StringComparison.Ordinal) && f.Contains("absolute path", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void Validate_AwsS3InvalidBucketName_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "Invalid_Bucket_Name!", // Invalid: contains invalid characters
                Region = "us-east-1"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("BucketName") && f.Contains("must contain only lowercase letters"));
    }

    [UnitTest]
    public void Validate_AwsS3BucketNameTooShort_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "ab", // Invalid: too short
                Region = "us-east-1"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("AwsS3.BucketName", StringComparison.Ordinal) && f.Contains("between 3 and 63 characters", StringComparison.Ordinal));
    }

    [UnitTest]
    public void Validate_AwsS3MissingRegion_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "valid-bucket",
                Region = "" // Invalid: empty
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Region") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_AwsS3PartialCredentials_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "valid-bucket",
                Region = "us-east-1",
                AccessKeyId = "AKIAIOSFODNN7EXAMPLE",
                SecretAccessKey = "" // Invalid: missing secret when access key is provided
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("SecretAccessKey") && f.Contains("must be provided when AccessKeyId is specified"));
    }

    [UnitTest]
    public void Validate_AzureBlobMissingConnectionString_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AzureBlob,
            AzureBlob = new AzureBlobOptions
            {
                ConnectionString = "", // Invalid: empty
                ContainerName = "valid-container"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("ConnectionString") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_CleanupIntervalTooShort_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.Local,
            EnableAutomaticCleanup = true,
            CleanupInterval = TimeSpan.FromMinutes(5), // Invalid: too short
            LocalStorage = new LocalStorageOptions
            {
                BasePath = "/tmp/storage"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("CleanupInterval", StringComparison.Ordinal) && f.Contains("between 900 seconds", StringComparison.Ordinal));
    }

    [UnitTest]
    public void Validate_MissingProviderConfiguration_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = null // Invalid: missing configuration for selected provider
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("AwsS3 configuration is required"));
    }

    [UnitTest]
    public void Validate_NullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _validator.Validate(null, null!));
    }

    [UnitTest]
    public void Validate_ValidServiceUrl_ReturnsSuccess()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "valid-bucket",
                Region = "us-east-1",
                ServiceUrl = "https://s3.us-east-1.amazonaws.com", // Valid HTTPS URL
                ForcePathStyle = false
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [UnitTest]
    public void Validate_InvalidServiceUrl_ReturnsFail()
    {
        // Arrange
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "valid-bucket",
                Region = "us-east-1",
                ServiceUrl = "not-a-valid-url" // Invalid URL
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("ServiceUrl") && f.Contains("must be a valid absolute URL"));
    }
}
