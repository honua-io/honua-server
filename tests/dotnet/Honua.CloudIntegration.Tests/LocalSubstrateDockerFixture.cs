// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Honua.ControlPlane;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.CloudIntegration.Tests;

public sealed class LocalSubstrateDockerFixture : IAsyncLifetime
{
    private const string ContainerRuntime = "docker";
    private const string BaseImage = "busybox:1.36";

    public string V1Image { get; } = "honua-ci/ls-replica:v1";

    public string V2Image { get; } = "honua-ci/ls-replica:v2";

    public const string V1Marker = "honua-revision-v1";

    public const string V2Marker = "honua-revision-v2";

    public const int ReplicaContainerPort = 8080;

    private ServiceProvider? _services;

    public bool Available { get; private set; }

    internal IContainerRuntimeClient Runtime { get; private set; } = null!;

    internal ILocalReplicaHealthProbe HealthProbe { get; private set; } = null!;

    public string RuntimeExecutable => ContainerRuntime;

    public async Task InitializeAsync()
    {
        try
        {
            if (!await IsDockerAvailableAsync().ConfigureAwait(false))
            {
                Available = false;
                return;
            }

            await BuildReplicaImageAsync(V1Image, V1Marker).ConfigureAwait(false);
            await BuildReplicaImageAsync(V2Image, V2Marker).ConfigureAwait(false);

            // Real seams the backend consumes. HttpLocalReplicaHealthProbe resolves the named
            // "control-plane-probe" HttpClient, so a minimal AddHttpClient container is enough.
            var services = new ServiceCollection();
            services.AddHttpClient();
            _services = services.BuildServiceProvider();

            Runtime = new ProcessContainerRuntimeClient(NullLogger<ProcessContainerRuntimeClient>.Instance);
            HealthProbe = new HttpLocalReplicaHealthProbe(
                _services.GetRequiredService<IHttpClientFactory>());

            Available = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Available = false;
            if (_services is not null)
            {
                await _services.DisposeAsync().ConfigureAwait(false);
                _services = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync().ConfigureAwait(false);
            _services = null;
        }
    }

    public async Task CleanupTargetAsync(string targetId)
    {
        var (_, stdout, _) = await RunProcessAsync(
            ContainerRuntime,
            ["ps", "-aq", "--filter", $"label={YarpRollingDeployBackend.LabelTarget}={targetId}"],
            CancellationToken.None).ConfigureAwait(false);

        foreach (var id in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await RunProcessAsync(ContainerRuntime, ["rm", "-f", id], CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static async Task<bool> WaitForBodyAsync(string url, string expectedBody, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(url).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
                    if (body.Contains(expectedBody, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Replica still starting.
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return false;
    }

    public static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> IsDockerAvailableAsync()
    {
        try
        {
            var (exitCode, _, _) = await RunProcessAsync(
                ContainerRuntime,
                ["version", "--format", "{{.Server.Version}}"],
                CancellationToken.None).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Any failure to launch/query the container runtime just means Docker is unavailable
            // here; callers use this as a skip-gate, not an assertion.
            return false;
        }
    }

    private static async Task BuildReplicaImageAsync(string image, string marker)
    {
        var contextDir = Directory.CreateTempSubdirectory("honua-ls-image").FullName;
        try
        {
            // busybox httpd over a one-file docroot: GET / returns the marker with HTTP 200, which
            // doubles as the health signal (probe path pointed at "/") and the version response.
            var dockerfile =
                $"""
                FROM {BaseImage}
                RUN mkdir -p /www && printf '%s' '{marker}' > /www/index.html
                ENTRYPOINT ["httpd", "-f", "-p", "{ReplicaContainerPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-h", "/www"]
                """;
            // "Dockerfile" is a fixed relative literal, so Path.Combine cannot drop contextDir here.
            await File.WriteAllTextAsync(Path.Join(contextDir, "Dockerfile"), dockerfile).ConfigureAwait(false);

            var (exitCode, _, stderr) = await RunProcessAsync(
                ContainerRuntime,
                ["build", "-t", image, contextDir],
                CancellationToken.None).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Failed to build replica image '{image}': {stderr.Trim()}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(contextDir, recursive: true);
            }
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
