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
    // The state object is process-wide and every mutation is serialized by _sharedLock.
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static readonly EmulatorSharedState SharedState = new();

    private sealed class EmulatorSharedState
    {
        public IContainer? LocalStackContainer { get; set; }
        public IContainer? AzuriteContainer { get; set; }
        public bool SharedInitialized { get; set; }
        public int SharedRefCount { get; set; }
    }

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
            if (!SharedState.SharedInitialized)
            {
                await StartContainersAsync();
                SetEnvironmentVariables();
                SharedState.SharedInitialized = true;
            }

            SharedState.SharedRefCount++;
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
            if (SharedState.SharedRefCount > 0)
            {
                SharedState.SharedRefCount--;
            }

            if (SharedState.SharedRefCount == 0 && SharedState.SharedInitialized)
            {
                await StopContainersAsync();
                ClearEnvironmentVariables();
                SharedState.SharedInitialized = false;
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
        SharedState.LocalStackContainer = new ContainerBuilder()
            .WithImage("localstack/localstack:3.6.0")
            .WithPortBinding(4566, true)
            .WithEnvironment("SERVICES", "s3")
            .WithEnvironment("DEFAULT_REGION", "us-east-1")
            .WithEnvironment("AWS_DEFAULT_REGION", "us-east-1")
            .WithEnvironment("AWS_ACCESS_KEY_ID", "test")
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", "test")
            .Build();

        // Start Azurite for Azure Blob emulation
        SharedState.AzuriteContainer = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/azure-storage/azurite:3.31.0")
            .WithPortBinding(10000, true)
            .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--blobPort", "10000", "--location", "/data", "--loose", "--skipApiVersionCheck")
            .Build();

        // Start containers in parallel
        await Task.WhenAll(
            SharedState.LocalStackContainer.StartAsync(),
            SharedState.AzuriteContainer.StartAsync()
        );

        var localStackPort = SharedState.LocalStackContainer.GetMappedPublicPort(4566);
        var azuritePort = SharedState.AzuriteContainer.GetMappedPublicPort(10000);
        await WaitForEmulatorsReadyAsync(localStackPort, azuritePort);
    }

    private static async Task StopContainersAsync()
    {
        var tasks = new List<Task>();

        if (SharedState.LocalStackContainer is not null)
        {
            tasks.Add(SharedState.LocalStackContainer.DisposeAsync().AsTask());
        }

        if (SharedState.AzuriteContainer is not null)
        {
            tasks.Add(SharedState.AzuriteContainer.DisposeAsync().AsTask());
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        SharedState.LocalStackContainer = null;
        SharedState.AzuriteContainer = null;
    }

    private static void SetEnvironmentVariables()
    {
        if (SharedState.LocalStackContainer is null || SharedState.AzuriteContainer is null)
        {
            throw new InvalidOperationException("Containers not started");
        }

        // LocalStack/S3 environment variables
        var localStackPort = SharedState.LocalStackContainer.GetMappedPublicPort(4566);
        Environment.SetEnvironmentVariable(LocalStackServiceUrlEnv, $"http://localhost:{localStackPort}");
        Environment.SetEnvironmentVariable(S3BucketEnv, "test-bucket");
        Environment.SetEnvironmentVariable(S3RegionEnv, "us-east-1");
        Environment.SetEnvironmentVariable(S3AccessKeyEnv, "test");
        Environment.SetEnvironmentVariable(S3SecretKeyEnv, "test");
        Environment.SetEnvironmentVariable(S3ForcePathStyleEnv, "true");

        // Azurite/Azure Blob environment variables
        var azuritePort = SharedState.AzuriteContainer.GetMappedPublicPort(10000);
        Environment.SetEnvironmentVariable(AzureBlobConnectionStringEnv,
            $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:{azuritePort}/devstoreaccount1;");
        Environment.SetEnvironmentVariable(AzureBlobContainerEnv, "test-container");
    }

    private static async Task WaitForEmulatorsReadyAsync(int localStackPort, int azuritePort)
    {
        var timeout = TimeSpan.FromSeconds(120);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var localStackReady = false;
            var azuriteReady = false;

            try
            {
                localStackReady = await IsLocalStackReadyAsync(httpClient, localStackPort);
            }
            catch (Exception ex) when (IsTransientHealthCheckFailure(ex))
            {
                lastError = ex;
            }

            try
            {
                azuriteReady = await IsAzuriteReadyAsync(httpClient, azuritePort);
            }
            catch (Exception ex) when (IsTransientHealthCheckFailure(ex))
            {
                lastError = ex;
            }

            if (localStackReady && azuriteReady)
            {
                return;
            }

            await Task.Delay(1000);
        }

        var errorDetails = lastError is null ? string.Empty : $" Last error: {lastError.Message}";
        throw new TimeoutException($"Timed out waiting for emulator containers to become ready.{errorDetails}");
    }

    private static async Task<bool> IsLocalStackReadyAsync(HttpClient client, int port)
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/_localstack/health");
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync();
        return body.Contains("\"s3\": \"available\"", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("\"s3\":\"available\"", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("\"s3\": \"running\"", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("\"s3\":\"running\"", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsAzuriteReadyAsync(HttpClient client, int port)
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/");
        return (int)response.StatusCode < 500;
    }

    private static bool IsTransientHealthCheckFailure(Exception exception)
    {
        return exception is HttpRequestException or TimeoutException or TaskCanceledException;
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
