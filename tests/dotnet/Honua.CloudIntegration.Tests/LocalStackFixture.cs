// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Honua.CloudIntegration.Tests;

public sealed class LocalStackFixture : IAsyncLifetime
{
    // Pin to the same image/tag the repo already uses in EmulatorFixture so the
    // Community service surface and health endpoint behaviour match what CI proves.
    private const string Image = "localstack/localstack:3.6.0";
    private const int EdgePort = 4566;

    // Community service set exercised by this harness. Kept minimal (only what a runnable test
    // actually uses) so the fixture does not imply coverage it does not have.
    private const string Services = "s3";

    public const string AccessKeyId = "test";

    public const string SecretAccessKey = "test";

    public const string Region = "us-east-1";

    private IContainer? _container;

    public bool Available { get; private set; }

    public string ServiceUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage(Image)
                .WithPortBinding(EdgePort, true)
                .WithEnvironment("SERVICES", Services)
                .WithEnvironment("DEFAULT_REGION", Region)
                .WithEnvironment("AWS_DEFAULT_REGION", Region)
                .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKeyId)
                .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretAccessKey)
                .Build();

            await _container.StartAsync();

            var mappedPort = _container.GetMappedPublicPort(EdgePort);
            ServiceUrl = $"http://localhost:{mappedPort}";

            await WaitForReadyAsync(mappedPort);
            Available = true;
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            // Docker not available (or the daemon refused the container): degrade to skip rather
            // than fail, exactly like KindClusterFixture when its env vars are absent.
            Available = false;
            if (_container is not null)
            {
                await _container.DisposeAsync();
                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    private static async Task WaitForReadyAsync(int port)
    {
        var timeout = TimeSpan.FromSeconds(120);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await httpClient.GetAsync(
                    $"http://localhost:{port}/_localstack/health");
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (IsServiceUp(body, "s3"))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                lastError = ex;
            }

            await Task.Delay(1000);
        }

        var detail = lastError is null ? string.Empty : $" Last error: {lastError.Message}";
        throw new TimeoutException(
            $"Timed out waiting for LocalStack service [{Services}] to become ready.{detail}");
    }

    // LocalStack reports each service state as "available", "running", or "disabled" in the
    // /_localstack/health JSON. Accept both available and running (lazily-started services
    // report "available" until first use), tolerating whitespace variations in the payload.
    private static bool IsServiceUp(string healthBody, string service)
        => healthBody.Contains($"\"{service}\": \"available\"", StringComparison.OrdinalIgnoreCase)
            || healthBody.Contains($"\"{service}\":\"available\"", StringComparison.OrdinalIgnoreCase)
            || healthBody.Contains($"\"{service}\": \"running\"", StringComparison.OrdinalIgnoreCase)
            || healthBody.Contains($"\"{service}\":\"running\"", StringComparison.OrdinalIgnoreCase);
}
