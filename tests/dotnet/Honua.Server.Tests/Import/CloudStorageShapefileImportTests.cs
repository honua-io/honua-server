// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.FileStorage;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Tests for shapefile import from AWS S3 cloud storage.
/// </summary>
[Collection("Emulators")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class AwsS3ShapefileImportTests : IAsyncLifetime
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
    public async Task Import_ZippedShapefile_UsingAwsS3_Completes()
    {
        var (payload, expectedFeatureCount) = ShapefileImportTestHelpers.GetShapefileZipPayload();
        var tableName = $"aws_shp_{Guid.NewGuid().ToString("N")[..8]}";

        var storage = _fixture.Services.GetRequiredService<ICloudFileStorage>();
        var importService = _fixture.Services.GetRequiredService<IFileImportService>();

        var result = await ShapefileImportTestHelpers.UploadAndImportAsync(
            storage,
            importService,
            payload,
            "aws-shapefile.zip",
            tableName);

        ShapefileImportTestHelpers.AssertCompleted(result, tableName, expectedFeatureCount);
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
        var exists = await AmazonS3Util.DoesS3BucketExistV2Async(client, options.BucketName);
        if (!exists)
        {
            await client.PutBucketAsync(new PutBucketRequest
            {
                BucketName = options.BucketName
            });
        }
    }

    private static AmazonS3Client CreateClient(AwsS3Options options)
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region),
            ForcePathStyle = options.ForcePathStyle
        };

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }

        return new AmazonS3Client(options.AccessKeyId, options.SecretAccessKey, config);
    }
}

/// <summary>
/// Tests for shapefile import from Azure Blob storage.
/// </summary>
[Collection("Emulators")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class AzureBlobShapefileImportTests : IAsyncLifetime
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
    public async Task Import_ZippedShapefile_UsingAzureBlob_Completes()
    {
        var (payload, expectedFeatureCount) = ShapefileImportTestHelpers.GetShapefileZipPayload();
        var tableName = $"az_shp_{Guid.NewGuid().ToString("N")[..8]}";

        var storage = _fixture.Services.GetRequiredService<ICloudFileStorage>();
        var importService = _fixture.Services.GetRequiredService<IFileImportService>();

        var result = await ShapefileImportTestHelpers.UploadAndImportAsync(
            storage,
            importService,
            payload,
            "azure-shapefile.zip",
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

internal static class ShapefileImportTestHelpers
{
    private const string UseBundledShapefileEnv = "HONUA_TEST_USE_BUNDLED_SHAPEFILE";
    private const string ShapefileZipFileName = "Extreme_Tsunami_Evacuation_Zones.zip";

    internal static (byte[] payload, int? expectedFeatureCount) GetShapefileZipPayload()
    {
        var useBundled = bool.TryParse(Environment.GetEnvironmentVariable(UseBundledShapefileEnv), out var flag) && flag;
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "TestData", ShapefileZipFileName);
        if (useBundled && File.Exists(bundledPath))
        {
            return (File.ReadAllBytes(bundledPath), null);
        }

        return CreateSampleShapefileZip();
    }

    internal static MultipartFormDataContent CreateImportContent(byte[] payload, string fileName, string tableName)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(payload);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = fileName
        };
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        content.Add(fileContent);
        content.Add(new StringContent(tableName), "TableName");
        content.Add(new StringContent("4326"), "SourceSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");
        content.Add(new StringContent("true"), "ForceBackground");

        return content;
    }

    internal static async Task<string> ExtractJobIdAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("jobId").GetString() ?? throw new InvalidOperationException("Job ID missing.");
    }

    internal static async Task<JsonElement> WaitForCompletionAsync(HttpClient client, string jobId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/v1/admin/import/jobs/{jobId}");
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(250);
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var status = ReadStatus(document.RootElement.GetProperty("status"));

            if (status == ImportStatus.Completed)
            {
                return document.RootElement.Clone();
            }

            if (status is ImportStatus.Failed or ImportStatus.Cancelled)
            {
                var errorMessage = document.RootElement.TryGetProperty("errorMessage", out var error)
                    ? error.GetString()
                    : "Import failed";
                throw new InvalidOperationException($"Import job {jobId} {status}: {errorMessage}");
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Import job {jobId} did not complete within {timeout.TotalSeconds} seconds.");
    }

    internal static void AssertCompleted(JsonElement progress, string tableName, int? expectedFeatureCount)
    {
        progress.GetProperty("tableName").GetString().Should().Be(tableName);
        progress.GetProperty("format").GetString().Should().Be("Shapefile");

        if (expectedFeatureCount.HasValue)
        {
            progress.GetProperty("featuresProcessed").GetInt32().Should().Be(expectedFeatureCount.Value);
        }
    }

    internal static void AssertCompleted(ImportResult result, string tableName, int? expectedFeatureCount)
    {
        result.Success.Should().BeTrue();
        result.TableName.Should().Be(tableName);
        result.Format.Should().Be(SupportedFileFormat.Shapefile);

        if (expectedFeatureCount.HasValue)
        {
            result.FeatureCount.Should().Be(expectedFeatureCount.Value);
        }
    }

    private static ImportStatus ReadStatus(JsonElement statusElement)
    {
        return statusElement.ValueKind switch
        {
            JsonValueKind.String => Enum.TryParse<ImportStatus>(statusElement.GetString(), out var parsed)
                ? parsed
                : throw new InvalidOperationException("Import status is invalid."),
            JsonValueKind.Number => (ImportStatus)statusElement.GetInt32(),
            _ => throw new InvalidOperationException("Import status is invalid.")
        };
    }

    private static (byte[] payload, int? expectedFeatureCount) CreateSampleShapefileZip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"honua-test-shp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var shpPath = Path.Combine(tempDir, "sample.shp");
            var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
            var features = new List<IFeature>
            {
                new Feature(geometryFactory.CreatePoint(new Coordinate(-122.4, 37.7)),
                    new AttributesTable { { "name", "One" }, { "id", 1 } }),
                new Feature(geometryFactory.CreatePoint(new Coordinate(-122.5, 37.8)),
                    new AttributesTable { { "name", "Two" }, { "id", 2 } })
            };

            const string projection = "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]";
            Shapefile.WriteAllFeatures(features, shpPath, projection, Encoding.UTF8);

            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var extension in new[] { ".shp", ".shx", ".dbf", ".prj" })
                {
                    var path = Path.ChangeExtension(shpPath, extension);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var fileStream = File.OpenRead(path);
                    fileStream.CopyTo(entryStream);
                }
            }

            return (zipStream.ToArray(), features.Count);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    internal static async Task<ImportResult> UploadAndImportAsync(
        ICloudFileStorage storage,
        IFileImportService importService,
        byte[] payload,
        string fileName,
        string tableName)
    {
        await using var stream = new MemoryStream(payload);
        var uploadRequest = new FileUploadRequest
        {
            Content = stream,
            FileName = fileName,
            ContentType = "application/zip",
            SizeBytes = payload.Length,
            TimeToLive = TimeSpan.FromHours(1),
            Folder = "imports"
        };

        var uploadResult = await storage.UploadAsync(uploadRequest);
        if (!uploadResult.Success || uploadResult.File == null)
        {
            throw new InvalidOperationException($"Upload failed: {uploadResult.ErrorMessage ?? "Unknown error"}");
        }

        var cloudFileId = uploadResult.File.FileId;

        try
        {
            var importRequest = new ImportRequest
            {
                CloudFileId = cloudFileId,
                FileName = fileName,
                TableName = tableName,
                SourceSrid = 4326,
                TargetSrid = 4326,
                OverwriteExisting = true
            };

            return await importService.ImportFileAsync(importRequest);
        }
        finally
        {
            await storage.DeleteAsync(cloudFileId);
        }
    }
}
