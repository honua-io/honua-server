// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Testcontainers fixture that boots a single LocalStack Community container and exposes its
/// edge endpoint. Only the free (Community) service set is enabled — currently S3, which the
/// <see cref="S3ArtifactRoundTripCloudIntegrationTests"/> exercises end-to-end. No paid
/// LocalStack Pro token is required.
///
/// When Docker is unavailable (for example a developer box without a daemon) container startup
/// fails; the fixture catches that, reports <see cref="Available"/> = false, and the dependent
/// tests skip (never fail) — mirroring <see cref="KindClusterFixture"/>'s skip-when-absent
/// behaviour so the project still compiles and runs everywhere.
///
/// Pins the same image/tag the repo already uses in <c>Honua.TestKit.EmulatorFixture</c> (port
/// 4566, <c>/_localstack/health</c> probe) so the harness reuses the repo's proven LocalStack
/// Community wiring.
/// </summary>
public sealed class LocalStackFixture : IAsyncLifetime
{
    // Pin to the same image/tag the repo already uses in EmulatorFixture so the
    // Community service surface and health endpoint behaviour match what CI proves.
    private const string Image = "localstack/localstack:3.6.0";
    private const int EdgePort = 4566;

    // Community service set exercised by this harness. Kept minimal (only what a runnable test
    // actually uses) so the fixture does not imply coverage it does not have.
    private const string Services = "s3";

    /// <summary>
    /// Static credential pair LocalStack accepts for SigV4 signing. LocalStack does not
    /// verify credentials, but the SDK still requires non-empty values when an explicit
    /// endpoint is configured.
    /// </summary>
    public const string AccessKeyId = "test";

    /// <summary>
    /// Static secret access key paired with <see cref="AccessKeyId"/>.
    /// </summary>
    public const string SecretAccessKey = "test";

    /// <summary>
    /// Region used for every emulated AWS client. Matches the repo's existing S3 emulator tests.
    /// </summary>
    public const string Region = "us-east-1";

    private IContainer? _container;

    /// <summary>
    /// True when the LocalStack container started and reported its Community services ready, so
    /// the dependent integration tests can run. False when Docker is unavailable.
    /// </summary>
    public bool Available { get; private set; }

    /// <summary>
    /// Edge service URL (for example <c>http://localhost:32789</c>) used as the SDK
    /// <c>ServiceURL</c> override for every emulated AWS client. Empty when unavailable.
    /// </summary>
    public string ServiceUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Boots LocalStack Community. When Docker is unavailable the failure is swallowed and
    /// <see cref="Available"/> stays false so the dependent tests skip instead of failing.
    /// </summary>
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

    /// <summary>
    /// Stops and disposes the LocalStack container.
    /// </summary>
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
