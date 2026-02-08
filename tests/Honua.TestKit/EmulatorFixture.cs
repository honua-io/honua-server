// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Honua.TestKit;

/// <summary>
/// Shared emulator fixture for cloud storage tests.
/// Manages LocalStack (S3) and Azurite (Azure Blob) containers.
/// </summary>
public sealed class EmulatorFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static IContainer? _localStackContainer;
    private static IContainer? _azuriteContainer;
    private static bool _sharedInitialized;
    private static int _sharedRefCount;

    // Environment variable names expected by tests
    private const string LocalStackServiceUrlEnv = "HONUA_TEST_S3_SERVICE_URL";
    private const string S3BucketEnv = "HONUA_TEST_S3_BUCKET";
    private const string S3RegionEnv = "HONUA_TEST_S3_REGION";
    private const string S3AccessKeyEnv = "HONUA_TEST_S3_ACCESS_KEY";
    private const string S3SecretKeyEnv = "HONUA_TEST_S3_SECRET_KEY";
    private const string S3ForcePathStyleEnv = "HONUA_TEST_S3_FORCE_PATH_STYLE";

    private const string AzureBlobConnectionStringEnv = "HONUA_TEST_AZURE_BLOB_CONNECTION_STRING";
    private const string AzureBlobContainerEnv = "HONUA_TEST_AZURE_BLOB_CONTAINER";

    public async Task InitializeAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (!_sharedInitialized)
            {
                await StartContainersAsync();
                SetEnvironmentVariables();
                _sharedInitialized = true;
            }

            _sharedRefCount++;
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (_sharedRefCount > 0)
            {
                _sharedRefCount--;
            }

            if (_sharedRefCount == 0 && _sharedInitialized)
            {
                await StopContainersAsync();
                ClearEnvironmentVariables();
                _sharedInitialized = false;
            }
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    private static async Task StartContainersAsync()
    {
        // Start LocalStack for S3 emulation
        _localStackContainer = new ContainerBuilder()
            .WithImage("localstack/localstack:3.6.0")
            .WithPortBinding(4566, true)
            .WithEnvironment("SERVICES", "s3")
            .WithEnvironment("DEFAULT_REGION", "us-east-1")
            .WithEnvironment("AWS_DEFAULT_REGION", "us-east-1")
            .WithEnvironment("AWS_ACCESS_KEY_ID", "test")
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", "test")
            .Build();

        // Start Azurite for Azure Blob emulation
        _azuriteContainer = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/azure-storage/azurite:3.31.0")
            .WithPortBinding(10000, true)
            .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--blobPort", "10000", "--location", "/data", "--loose", "--skipApiVersionCheck")
            .Build();

        // Start containers in parallel
        await Task.WhenAll(
            _localStackContainer.StartAsync(),
            _azuriteContainer.StartAsync()
        );

        // Give containers time to initialize
        await Task.Delay(TimeSpan.FromSeconds(5));
    }

    private static async Task StopContainersAsync()
    {
        var tasks = new List<Task>();

        if (_localStackContainer is not null)
        {
            tasks.Add(_localStackContainer.DisposeAsync().AsTask());
        }

        if (_azuriteContainer is not null)
        {
            tasks.Add(_azuriteContainer.DisposeAsync().AsTask());
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        _localStackContainer = null;
        _azuriteContainer = null;
    }

    private static void SetEnvironmentVariables()
    {
        if (_localStackContainer is null || _azuriteContainer is null)
        {
            throw new InvalidOperationException("Containers not started");
        }

        // LocalStack/S3 environment variables
        var localStackPort = _localStackContainer.GetMappedPublicPort(4566);
        Environment.SetEnvironmentVariable(LocalStackServiceUrlEnv, $"http://localhost:{localStackPort}");
        Environment.SetEnvironmentVariable(S3BucketEnv, "test-bucket");
        Environment.SetEnvironmentVariable(S3RegionEnv, "us-east-1");
        Environment.SetEnvironmentVariable(S3AccessKeyEnv, "test");
        Environment.SetEnvironmentVariable(S3SecretKeyEnv, "test");
        Environment.SetEnvironmentVariable(S3ForcePathStyleEnv, "true");

        // Azurite/Azure Blob environment variables
        var azuritePort = _azuriteContainer.GetMappedPublicPort(10000);
        Environment.SetEnvironmentVariable(AzureBlobConnectionStringEnv,
            $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:{azuritePort}/devstoreaccount1;");
        Environment.SetEnvironmentVariable(AzureBlobContainerEnv, "test-container");
    }

    private static void ClearEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable(LocalStackServiceUrlEnv, null);
        Environment.SetEnvironmentVariable(S3BucketEnv, null);
        Environment.SetEnvironmentVariable(S3RegionEnv, null);
        Environment.SetEnvironmentVariable(S3AccessKeyEnv, null);
        Environment.SetEnvironmentVariable(S3SecretKeyEnv, null);
        Environment.SetEnvironmentVariable(S3ForcePathStyleEnv, null);
        Environment.SetEnvironmentVariable(AzureBlobConnectionStringEnv, null);
        Environment.SetEnvironmentVariable(AzureBlobContainerEnv, null);
    }
}
