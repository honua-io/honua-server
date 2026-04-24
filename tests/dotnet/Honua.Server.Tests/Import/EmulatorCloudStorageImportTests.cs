// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.FileStorage;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Tests for cloud storage import using real emulators (LocalStack for S3, Azurite for Azure Blob).
/// Verifies the cloud upload/download staging path used during import processing.
/// </summary>
[Collection("Emulators")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public sealed class EmulatorAwsS3CloudStorageImportTests : IAsyncLifetime
{
    private const string BucketEnv = "HONUA_TEST_S3_BUCKET";
    private const string RegionEnv = "HONUA_TEST_S3_REGION";
    private const string AccessKeyEnv = "HONUA_TEST_S3_ACCESS_KEY";
    private const string SecretKeyEnv = "HONUA_TEST_S3_SECRET_KEY";
    private const string ServiceUrlEnv = "HONUA_TEST_S3_SERVICE_URL";
    private const string ForcePathStyleEnv = "HONUA_TEST_S3_FORCE_PATH_STYLE";

    private WebAppFixture _fixture = null!;
    private AwsS3Options _options = null!;

    public async Task InitializeAsync()
    {
        _options = GetAwsOptionsOrSkip();
        _fixture = new WebAppFixture()
            .ConfigureServices(services => ConfigureAwsStorage(services, _options));
        await _fixture.InitializeAsync();
        await EnsureBucketExistsAsync(_options);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv, ForcePathStyleEnv)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_GeoJsonViaS3CloudStaging_ImportsSuccessfully()
    {
        var geoJsonContent = """
            {
                "type": "FeatureCollection",
                "features": [
                    {
                        "type": "Feature",
                        "geometry": {
                            "type": "Point",
                            "coordinates": [-122.4194, 37.7749]
                        },
                        "properties": { "name": "S3 Cloud Staging Test" }
                    }
                ]
            }
            """u8.ToArray();

        var tableName = $"s3_staging_{Guid.NewGuid().ToString("N")[..8]}";
        var storage = _fixture.Services.GetRequiredService<ICloudFileStorage>();
        var importService = _fixture.Services.GetRequiredService<IFileImportService>();

        await using var stream = new MemoryStream(geoJsonContent);
        var uploadRequest = new FileUploadRequest
        {
            Content = stream,
            FileName = "s3-staging-test.geojson",
            ContentType = "application/json",
            SizeBytes = geoJsonContent.Length,
            TimeToLive = TimeSpan.FromHours(1),
            Folder = "imports"
        };

        var uploadResult = await storage.UploadAsync(uploadRequest);
        uploadResult.Success.Should().BeTrue();
        uploadResult.File.Should().NotBeNull();
        var cloudFileId = uploadResult.File!.FileId;

        try
        {
            var importRequest = new ImportRequest
            {
                CloudFileId = cloudFileId,
                FileName = "s3-staging-test.geojson",
                TableName = tableName,
                SourceSrid = 4326,
                TargetSrid = 4326,
                OverwriteExisting = true
            };

            var result = await importService.ImportFileAsync(importRequest);
            result.Success.Should().BeTrue();
            result.TableName.Should().Be(tableName);
            result.Format.Should().Be(SupportedFileFormat.GeoJson);
            result.FeatureCount.Should().BeGreaterThan(0);
        }
        finally
        {
            await storage.DeleteAsync(cloudFileId);
        }
    }

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv, ForcePathStyleEnv)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_MultiLayerFileGdbViaS3CloudStaging_FailsWithoutMergingLayers()
    {
        var fileGdbPath = Path.Combine(AppContext.BaseDirectory, "TestData", "FileGdb", "testopenfilegdb.gdb.zip");
        if (!File.Exists(fileGdbPath))
        {
            return; // Skip if test data not available
        }

        var fileGdbBytes = await File.ReadAllBytesAsync(fileGdbPath);
        var tableName = $"s3_fgdb_{Guid.NewGuid().ToString("N")[..8]}";
        var storage = _fixture.Services.GetRequiredService<ICloudFileStorage>();
        var importService = _fixture.Services.GetRequiredService<IFileImportService>();

        await using var stream = new MemoryStream(fileGdbBytes);
        var uploadRequest = new FileUploadRequest
        {
            Content = stream,
            FileName = "test.gdb.zip",
            ContentType = "application/zip",
            SizeBytes = fileGdbBytes.Length,
            TimeToLive = TimeSpan.FromHours(1),
            Folder = "imports"
        };

        var uploadResult = await storage.UploadAsync(uploadRequest);
        uploadResult.Success.Should().BeTrue();
        uploadResult.File.Should().NotBeNull();
        var cloudFileId = uploadResult.File!.FileId;

        try
        {
            var importRequest = new ImportRequest
            {
                CloudFileId = cloudFileId,
                FileName = "test.gdb.zip",
                TableName = tableName,
                SourceSrid = 4326,
                TargetSrid = 4326,
                OverwriteExisting = true
            };

            var result = await importService.ImportFileAsync(importRequest);
            result.Success.Should().BeFalse();
            result.TableName.Should().Be(tableName);
            result.Format.Should().Be(SupportedFileFormat.FileGdb);
            result.ErrorMessage.Should().Contain("multiple feature classes");
        }
        finally
        {
            await storage.DeleteAsync(cloudFileId);
        }
    }

    private static AwsS3Options GetAwsOptionsOrSkip()
    {
        return new AwsS3Options
        {
            BucketName = GetRequiredEnv(BucketEnv),
            Region = GetRequiredEnv(RegionEnv),
            AccessKeyId = GetRequiredEnv(AccessKeyEnv),
            SecretAccessKey = GetRequiredEnv(SecretKeyEnv),
            ServiceUrl = Environment.GetEnvironmentVariable(ServiceUrlEnv),
            ForcePathStyle = bool.TryParse(Environment.GetEnvironmentVariable(ForcePathStyleEnv), out var parsed)
                && parsed
        };
    }

    private static string GetRequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required for S3 emulator tests.");
        }

        return value;
    }

    private static void ConfigureAwsStorage(IServiceCollection services, AwsS3Options options)
    {
        services.RemoveAll<ICloudFileStorage>();
        services.RemoveAll<IOptions<CloudStorageOptions>>();
        services.AddSingleton<IOptions<CloudStorageOptions>>(Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = options
        }));
        services.AddSingleton<ICloudFileStorage, AwsS3FileStorage>();
    }

    private static async Task EnsureBucketExistsAsync(AwsS3Options options)
    {
        using var client = CreateClient(options);
        var exists = await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(client, options.BucketName);
        if (!exists)
        {
            await client.PutBucketAsync(new Amazon.S3.Model.PutBucketRequest
            {
                BucketName = options.BucketName
            });
        }
    }

    private static Amazon.S3.AmazonS3Client CreateClient(AwsS3Options options)
    {
        var config = new Amazon.S3.AmazonS3Config
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region),
            ForcePathStyle = options.ForcePathStyle
        };

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }

        return new Amazon.S3.AmazonS3Client(options.AccessKeyId, options.SecretAccessKey, config);
    }
}

/// <summary>
/// Tests for cloud storage import using Azurite (Azure Blob emulator).
/// Verifies the Azure Blob upload/download staging path used during import processing.
/// </summary>
[Collection("Emulators")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public sealed class EmulatorAzureBlobCloudStorageImportTests : IAsyncLifetime
{
    private const string ConnectionStringEnv = "HONUA_TEST_AZURE_BLOB_CONNECTION_STRING";
    private const string ContainerEnv = "HONUA_TEST_AZURE_BLOB_CONTAINER";

    private WebAppFixture _fixture = null!;
    private AzureBlobOptions _options = null!;

    public async Task InitializeAsync()
    {
        _options = GetAzureOptionsOrSkip();
        _fixture = new WebAppFixture()
            .ConfigureServices(services => ConfigureAzureStorage(services, _options));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [EmulatorTest(ConnectionStringEnv, ContainerEnv)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_GeoJsonViaAzureBlobStaging_ImportsSuccessfully()
    {
        var geoJsonContent = """
            {
                "type": "FeatureCollection",
                "features": [
                    {
                        "type": "Feature",
                        "geometry": {
                            "type": "Point",
                            "coordinates": [-118.2437, 34.0522]
                        },
                        "properties": { "name": "Azure Blob Staging Test" }
                    }
                ]
            }
            """u8.ToArray();

        var tableName = $"az_staging_{Guid.NewGuid().ToString("N")[..8]}";
        var storage = _fixture.Services.GetRequiredService<ICloudFileStorage>();
        var importService = _fixture.Services.GetRequiredService<IFileImportService>();

        await using var stream = new MemoryStream(geoJsonContent);
        var uploadRequest = new FileUploadRequest
        {
            Content = stream,
            FileName = "azure-staging-test.geojson",
            ContentType = "application/json",
            SizeBytes = geoJsonContent.Length,
            TimeToLive = TimeSpan.FromHours(1),
            Folder = "imports"
        };

        var uploadResult = await storage.UploadAsync(uploadRequest);
        uploadResult.Success.Should().BeTrue();
        uploadResult.File.Should().NotBeNull();
        var cloudFileId = uploadResult.File!.FileId;

        try
        {
            var importRequest = new ImportRequest
            {
                CloudFileId = cloudFileId,
                FileName = "azure-staging-test.geojson",
                TableName = tableName,
                SourceSrid = 4326,
                TargetSrid = 4326,
                OverwriteExisting = true
            };

            var result = await importService.ImportFileAsync(importRequest);
            result.Success.Should().BeTrue();
            result.TableName.Should().Be(tableName);
            result.Format.Should().Be(SupportedFileFormat.GeoJson);
            result.FeatureCount.Should().BeGreaterThan(0);
        }
        finally
        {
            await storage.DeleteAsync(cloudFileId);
        }
    }

    [EmulatorTest(ConnectionStringEnv, ContainerEnv)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_ShapefileViaAzureBlobStaging_ImportsSuccessfully()
    {
        var (payload, expectedFeatureCount) = ShapefileImportTestHelpers.GetShapefileZipPayload();
        var tableName = $"az_shp_staging_{Guid.NewGuid().ToString("N")[..8]}";

        var storage = _fixture.Services.GetRequiredService<ICloudFileStorage>();
        var importService = _fixture.Services.GetRequiredService<IFileImportService>();

        var result = await ShapefileImportTestHelpers.UploadAndImportAsync(
            storage,
            importService,
            payload,
            "azure-shapefile-staging.zip",
            tableName);

        ShapefileImportTestHelpers.AssertCompleted(result, tableName, expectedFeatureCount);
    }

    private static AzureBlobOptions GetAzureOptionsOrSkip()
    {
        return new AzureBlobOptions
        {
            ConnectionString = GetRequiredEnv(ConnectionStringEnv),
            ContainerName = GetRequiredEnv(ContainerEnv)
        };
    }

    private static string GetRequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required for Azure emulator tests.");
        }

        return value;
    }

    private static void ConfigureAzureStorage(IServiceCollection services, AzureBlobOptions options)
    {
        services.RemoveAll<ICloudFileStorage>();
        services.RemoveAll<IOptions<CloudStorageOptions>>();
        services.AddSingleton<IOptions<CloudStorageOptions>>(Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AzureBlob,
            AzureBlob = options
        }));
        services.AddSingleton<ICloudFileStorage, AzureBlobFileStorage>();
    }
}
