// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.FileStorage;

/// <summary>
/// Integration tests for AzureBlobFileStorage using Azurite.
/// </summary>
[Collection("Emulators")]
public sealed class AzureBlobFileStorageTests
{
    private const string ConnectionStringEnv = "HONUA_TEST_AZURE_BLOB_CONNECTION_STRING";
    private const string ContainerEnv = "HONUA_TEST_AZURE_BLOB_CONTAINER";

    [EmulatorTest(ConnectionStringEnv, ContainerEnv)]
    public async Task UploadDownloadDelete_RoundTripsContent()
    {
        var storage = CreateStorage();
        var content = "Azurite content"u8.ToArray();
        var request = new ByteArrayUploadRequest
        {
            Content = content,
            FileName = "azurite-test.txt",
            ContentType = "text/plain"
        };

        string? fileId = null;
        try
        {
            var result = await storage.UploadAsync(request);
            result.Success.Should().BeTrue();
            result.File.Should().NotBeNull();
            fileId = result.File!.FileId;

            var downloaded = await storage.DownloadBytesAsync(fileId);
            downloaded.Should().NotBeNull();
            downloaded.Should().Equal(content);

            var metadata = await storage.GetMetadataAsync(fileId);
            metadata.Should().NotBeNull();
            metadata!.FileName.Should().Be("azurite-test.txt");
        }
        finally
        {
            if (!string.IsNullOrEmpty(fileId))
            {
                await storage.DeleteAsync(fileId);
            }
        }
    }

    private static AzureBlobFileStorage CreateStorage()
    {
        var connectionString = GetRequiredEnv(ConnectionStringEnv);
        var containerName = GetRequiredEnv(ContainerEnv);

        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AzureBlob,
            AzureBlob = new AzureBlobOptions
            {
                ConnectionString = connectionString,
                ContainerName = containerName
            }
        };

        return new AzureBlobFileStorage(Options.Create(options), NullLogger<AzureBlobFileStorage>.Instance);
    }

    private static string GetRequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required for Azure Blob emulator tests.");
        }

        return value;
    }
}
