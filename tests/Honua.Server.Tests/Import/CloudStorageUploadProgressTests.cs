// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.FileStorage;
using Honua.Server.Tests.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Tests for upload progress tracking with AWS S3 cloud storage.
/// </summary>
[Collection("Emulators")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public sealed class AwsS3UploadProgressTests : IAsyncLifetime
{
    private const string BucketEnv = "HONUA_TEST_S3_BUCKET";
    private const string RegionEnv = "HONUA_TEST_S3_REGION";
    private const string AccessKeyEnv = "HONUA_TEST_S3_ACCESS_KEY";
    private const string SecretKeyEnv = "HONUA_TEST_S3_SECRET_KEY";
    private const string ServiceUrlEnv = "HONUA_TEST_S3_SERVICE_URL";
    private const string ForcePathStyleEnv = "HONUA_TEST_S3_FORCE_PATH_STYLE";

    private WebAppFixture _fixture = null!;
    private HttpClient _client = null!;
    private AwsS3Options _options = null!;

    public async Task InitializeAsync()
    {
        _options = GetAwsOptionsOrSkip();
        _fixture = new WebAppFixture()
            .ConfigureServices(services => ConfigureAwsStorage(services, _options));
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
        await EnsureBucketExistsAsync(_options);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv, ForcePathStyleEnv)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithProgress_UsesLocalstackCli_And_CompletesImport()
    {
        var tableName = $"upload_progress_{Guid.NewGuid().ToString("N")[..8]}";
        const string fileName = "progress_upload.geojson";
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
                    "properties": { "name": "ProgressTest" }
                }
            ]
        }
        """;

        using var content = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = fileName
        };
        content.Add(fileContent);
        content.Add(new StringContent(tableName), "TableName");
        content.Add(new StringContent("4326"), "SourceSrid");
        content.Add(new StringContent("4326"), "TargetSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");
        content.Add(new StringContent("true"), "ForceBackground");
        content.Add(new StringContent("true"), "TrackProgress");

        using var response = await _client.PostAsync("/api/v1/admin/import/upload", content);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var responseDoc = JsonDocument.Parse(responseContent);
        var jobId = responseDoc.RootElement.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException("Job ID missing.");
        var uploadId = responseDoc.RootElement.GetProperty("uploadId").GetString()
            ?? throw new InvalidOperationException("Upload ID missing.");

        var uploadProgress = await WaitForUploadCompletionAsync(_client, uploadId, TimeSpan.FromSeconds(60));
        var cloudFileId = uploadProgress.GetProperty("cloudFileId").GetString();
        cloudFileId.Should().NotBeNullOrWhiteSpace();

        using var headDoc = await LocalstackCli.HeadObjectAsync(_options, cloudFileId!);
        var metadataElement = headDoc.RootElement.GetProperty("Metadata");
        var storedFileName = ReadMetadataValue(metadataElement, "honua-file-name");
        storedFileName.Should().Be(fileName);

        var importProgress = await WaitForImportJobProgressAsync(_client, jobId, TimeSpan.FromSeconds(30));
        ReadImportStatus(importProgress.GetProperty("status"))
            .Should()
            .BeOneOf(ImportStatus.Queued, ImportStatus.Processing, ImportStatus.Completed);
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

    private static async Task<JsonElement> WaitForUploadCompletionAsync(HttpClient client, string uploadId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/v1/admin/import/uploads/{uploadId}/progress");
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(250);
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var status = ReadStatus(document.RootElement.GetProperty("status"));

            if (status == OperationStatus.Completed)
            {
                return document.RootElement.Clone();
            }

            if (status is OperationStatus.Failed or OperationStatus.Cancelled)
            {
                var errorMessage = document.RootElement.TryGetProperty("errorMessage", out var error)
                    ? error.GetString()
                    : "Upload failed";
                throw new InvalidOperationException($"Upload {uploadId} {status}: {errorMessage}");
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Upload {uploadId} did not complete within {timeout.TotalSeconds} seconds.");
    }

    private static async Task<JsonElement> WaitForImportJobProgressAsync(HttpClient client, string jobId, TimeSpan timeout)
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
            var status = ReadImportStatus(document.RootElement.GetProperty("status"));

            if (status is ImportStatus.Failed or ImportStatus.Cancelled)
            {
                var errorMessage = document.RootElement.TryGetProperty("errorMessage", out var error)
                    ? error.GetString()
                    : "Operation failed";
                throw new InvalidOperationException($"Import job {jobId} {status}: {errorMessage}");
            }

            return document.RootElement.Clone();
        }

        throw new TimeoutException($"Import job {jobId} did not report progress within {timeout.TotalSeconds} seconds.");
    }

    private static OperationStatus ReadStatus(JsonElement statusElement)
    {
        return statusElement.ValueKind switch
        {
            JsonValueKind.String => Enum.TryParse<OperationStatus>(statusElement.GetString(), out var parsed)
                ? parsed
                : throw new InvalidOperationException("Operation status is invalid."),
            JsonValueKind.Number => (OperationStatus)statusElement.GetInt32(),
            _ => throw new InvalidOperationException("Operation status is invalid.")
        };
    }

    private static ImportStatus ReadImportStatus(JsonElement statusElement)
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

    private static string? ReadMetadataValue(JsonElement metadataElement, string key)
    {
        foreach (var property in metadataElement.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
