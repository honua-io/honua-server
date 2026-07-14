// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Honua.ControlPlane;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Docker fixture for the ADR-0060 substrate-neutral serving lane (#2166, #2457). Builds two tiny
/// disposable HTTP replica images that stand in for two revisions of the Honua serving process, and
/// exposes the REAL container-runtime and local-health-probe seams
/// (<see cref="ProcessContainerRuntimeClient"/> / <see cref="HttpLocalReplicaHealthProbe"/>) that the
/// <see cref="YarpRollingDeployBackend"/> drives against a real <c>docker</c> daemon.
/// </summary>
/// <remarks>
/// <para>
/// Image choice: a 3-line <c>busybox:1.36</c> Dockerfile running <c>httpd</c> over a one-file docroot
/// whose <c>index.html</c> is the revision marker. This mirrors the existing kind lane's busybox worker
/// images (no new base-image dependency), serves HTTP 200 on <c>/</c> (satisfying the backend's default
/// <c>/healthz/ready</c> health probe once the probe path is pointed at <c>/</c>), and returns an
/// identifiable version body so the zero-downtime request loop can prove the v1→v2 flip. The two images
/// are built by the fixture itself (idempotent) so the lane is self-contained on any Docker host; the
/// workflow additionally pre-pulls <c>busybox</c> to warm the cache.
/// </para>
/// <para>
/// When Docker is unavailable the build fails, <see cref="Available"/> stays false, and the dependent
/// <c>[SkippableFact]</c> tests skip (never fail) — mirroring <see cref="KindClusterFixture"/> and
/// <see cref="LocalStackFixture"/>.
/// </para>
/// </remarks>
public sealed class LocalSubstrateDockerFixture : IAsyncLifetime
{
    private const string ContainerRuntime = "docker";
    private const string BaseImage = "busybox:1.36";

    /// <summary>Container image serving the "v1" revision marker on port 8080.</summary>
    public string V1Image { get; } = "honua-ci/ls-replica:v1";

    /// <summary>Container image serving the "v2" revision marker on port 8080.</summary>
    public string V2Image { get; } = "honua-ci/ls-replica:v2";

    /// <summary>Revision marker body served by <see cref="V1Image"/>.</summary>
    public const string V1Marker = "honua-revision-v1";

    /// <summary>Revision marker body served by <see cref="V2Image"/>.</summary>
    public const string V2Marker = "honua-revision-v2";

    /// <summary>Container port the replica images publish.</summary>
    public const int ReplicaContainerPort = 8080;

    private ServiceProvider? _services;

    /// <summary>True when Docker is available and both replica images built successfully.</summary>
    public bool Available { get; private set; }

    /// <summary>Real container runtime seam the rolling-deploy backend drives.</summary>
    internal IContainerRuntimeClient Runtime { get; private set; } = null!;

    /// <summary>Real loopback replica health-probe seam the rolling-deploy backend drives.</summary>
    internal ILocalReplicaHealthProbe HealthProbe { get; private set; } = null!;

    /// <summary>The container runtime executable name (<c>docker</c>).</summary>
    public string RuntimeExecutable => ContainerRuntime;

    /// <inheritdoc />
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
            // "control-plane-telemetry" HttpClient, so a minimal AddHttpClient container is enough.
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

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync().ConfigureAwait(false);
            _services = null;
        }
    }

    /// <summary>
    /// Force-removes every container carrying the supplied <c>honua.target</c> label. Best-effort
    /// cleanup so a failed scenario cannot leak the standby/active replicas it launched.
    /// </summary>
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

    /// <summary>
    /// Polls a URL until it returns HTTP 200 with the expected body, or the timeout elapses. Used to
    /// confirm a freshly launched replica container is actually serving before a scenario proceeds.
    /// </summary>
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

    /// <summary>Reserves an ephemeral loopback TCP port for a replica container's host binding.</summary>
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
        catch
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
            await File.WriteAllTextAsync(Path.Combine(contextDir, "Dockerfile"), dockerfile).ConfigureAwait(false);

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
            catch
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
