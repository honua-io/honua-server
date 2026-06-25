// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Testcontainers fixture that boots a single LocalStack container and exposes its
/// edge endpoint. The default (free) tier enables only the LocalStack Community service
/// set — S3, Lambda, CloudWatch, and CloudWatch Logs — which require no paid token.
///
/// When the optional <c>LOCALSTACK_AUTH_TOKEN</c> environment variable is present the
/// fixture additionally requests the Pro service set (AWS Batch + ECS emulation) and
/// passes the token through to the container. Pro-tier tests check
/// <see cref="ProEnabled"/> and skip (never fail) when the token is absent, so the free
/// CI lane stays green without a LocalStack subscription.
///
/// Mirrors <c>Honua.TestKit.EmulatorFixture</c> (same image/tag, port 4566, health probe)
/// so the harness shares the repo's proven LocalStack Community wiring.
/// </summary>
public sealed class LocalStackFixture : IAsyncLifetime
{
    // Pin to the same image/tag the repo already uses in EmulatorFixture so the
    // Community service surface and health endpoint behaviour match what CI proves.
    private const string Image = "localstack/localstack:3.6.0";
    private const int EdgePort = 4566;

    /// <summary>
    /// Name of the optional environment variable carrying a LocalStack Pro auth token.
    /// When set, Batch/ECS emulation is enabled and Pro-tier scenarios run; when unset,
    /// those scenarios skip.
    /// </summary>
    public const string AuthTokenEnvVar = "LOCALSTACK_AUTH_TOKEN";

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
    /// True when a LocalStack Pro auth token was supplied, so Batch/ECS emulation is available.
    /// </summary>
    public bool ProEnabled { get; private set; }

    /// <summary>
    /// Edge service URL (for example <c>http://localhost:32789</c>) used as the SDK
    /// <c>ServiceURL</c> override for every emulated AWS client.
    /// </summary>
    public string ServiceUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Boots LocalStack, enabling the Pro service set only when an auth token is present.
    /// </summary>
    public async Task InitializeAsync()
    {
        var authToken = Environment.GetEnvironmentVariable(AuthTokenEnvVar);
        ProEnabled = !string.IsNullOrWhiteSpace(authToken);

        // Community set is always available; Batch/ECS are Pro-only emulations. Requesting
        // them without a token would have LocalStack reject those service calls, so only add
        // them when the token unlocks them.
        var services = ProEnabled
            ? "s3,lambda,cloudwatch,logs,batch,ecs,iam,ec2"
            : "s3,lambda,cloudwatch,logs";

        var builder = new ContainerBuilder()
            .WithImage(Image)
            .WithPortBinding(EdgePort, true)
            .WithEnvironment("SERVICES", services)
            .WithEnvironment("DEFAULT_REGION", Region)
            .WithEnvironment("AWS_DEFAULT_REGION", Region)
            .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKeyId)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretAccessKey)
            // Lambda emulation needs a Docker socket to spawn function containers; LocalStack
            // detects the mounted socket automatically. The kind/Docker-in-CI host provides it.
            .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock");

        if (ProEnabled)
        {
            builder = builder.WithEnvironment(AuthTokenEnvVar, authToken!);
        }

        _container = builder.Build();
        await _container.StartAsync();

        var mappedPort = _container.GetMappedPublicPort(EdgePort);
        ServiceUrl = $"http://localhost:{mappedPort}";

        await WaitForReadyAsync(mappedPort, services);
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

    private static async Task WaitForReadyAsync(int port, string services)
    {
        var timeout = TimeSpan.FromSeconds(180);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // Only the always-present Community services are required for readiness; Pro services
        // may take longer to provision and are not gating for the free lane.
        var requiredServices = new[] { "s3", "lambda", "cloudwatch" };
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
                    if (requiredServices.All(svc => IsServiceUp(body, svc)))
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
            $"Timed out waiting for LocalStack services [{services}] to become ready.{detail}");
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
