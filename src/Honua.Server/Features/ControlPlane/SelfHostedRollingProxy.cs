// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yarp.ReverseProxy.Configuration;

namespace Honua.ControlPlane;

// When the proxy is disabled it is never wired into the request pipeline.
internal sealed class SelfHostedDeployOptions
{
    public const string SectionName = "ControlPlane:SelfHosted";

    public bool Enabled { get; set; }

    public string RouteId { get; set; } = "honua-selfhosted";

    public string ClusterId { get; set; } = "honua-selfhosted";

    public string RoutePath { get; set; } = "/{**catch-all}";

    public string ContainerRuntime { get; set; } = "docker";

    public string ContainerNamePrefix { get; set; } = "honua-app";

    public string Host { get; set; } = "127.0.0.1";

    public string HealthPath { get; set; } = "/healthz/ready";

    public int ActivePort { get; set; } = 18080;

    public int StandbyPort { get; set; } = 18081;

    public int ContainerPort { get; set; } = 8080;

    public int HealthProbeSamples { get; set; } = 3;

    public int HealthProbeTimeoutSeconds { get; set; } = 5;

    public int HealthProbeExpectedStatusCode { get; set; } = 200;

    public int DrainDelaySeconds { get; set; } = 5;
}

internal sealed class SelfHostedDeployOptionsValidator : OptionsValidator<SelfHostedDeployOptions>
{
    protected override void ValidateOptions(SelfHostedDeployOptions options, List<string> failures)
    {
        if (!options.Enabled)
        {
            return;
        }

        const string prefix = SelfHostedDeployOptions.SectionName;
        if (options.ActivePort <= 0 || options.StandbyPort <= 0)
        {
            failures.Add($"{prefix}:ActivePort and {prefix}:StandbyPort must be greater than 0 when Enabled.");
        }
        else if (options.ActivePort == options.StandbyPort)
        {
            failures.Add($"{prefix}:ActivePort and {prefix}:StandbyPort must differ.");
        }

        if (options.ContainerPort <= 0)
        {
            failures.Add($"{prefix}:ContainerPort must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(options.ContainerRuntime))
        {
            failures.Add($"{prefix}:ContainerRuntime cannot be empty.");
        }

        if (options.HealthProbeSamples <= 0)
        {
            failures.Add($"{prefix}:HealthProbeSamples must be greater than 0.");
        }

        if (options.HealthProbeTimeoutSeconds <= 0)
        {
            failures.Add($"{prefix}:HealthProbeTimeoutSeconds must be greater than 0.");
        }
    }
}

internal static class SelfHostedProxyReconstruction
{
    // Container labels do not show that cutover completed, so a protected operation is what proves traffic has moved.
    public static string? ResolveRunningDestination(
        IReadOnlyList<ContainerSummary> containers,
        SelfHostedDeployOptions options,
        IReadOnlyList<WorkflowOperationRecord>? activeDeployOperations = null)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(options);

        var prefix = string.IsNullOrWhiteSpace(options.ContainerNamePrefix) ? "honua-app" : options.ContainerNamePrefix;
        var host = string.IsNullOrWhiteSpace(options.Host) ? "127.0.0.1" : options.Host;
        var activeName = $"{prefix}-{options.ActivePort.ToString(CultureInfo.InvariantCulture)}";
        var standbyName = $"{prefix}-{options.StandbyPort.ToString(CultureInfo.InvariantCulture)}";

        var provenPromoted = activeDeployOperations?.Any(operation =>
            operation.Kind == WorkflowOperationKind.Deploy &&
            operation.Deploy is { TargetKind: DeployTargetKind.SelfHostedRolling, Protection: not null })
            ?? false;
        if (provenPromoted && IsRunning(containers, standbyName))
        {
            return Address(host, options.StandbyPort);
        }

        if (IsRunning(containers, activeName))
        {
            return Address(host, options.ActivePort);
        }

        if (IsRunning(containers, standbyName))
        {
            return Address(host, options.StandbyPort);
        }

        return null;
    }

    private static bool IsRunning(IReadOnlyList<ContainerSummary> containers, string name)
        => containers.Any(container => container.Running && string.Equals(container.Name, name, StringComparison.Ordinal));

    private static string Address(string host, int port)
        => $"http://{host}:{port.ToString(CultureInfo.InvariantCulture)}/";
}

internal sealed class YarpInMemoryProxyStateSwapper : IProxyStateSwapper
{
    private static readonly IReadOnlyDictionary<string, string> NoLabelSelectors =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly InMemoryConfigProvider _configProvider;
    private readonly IContainerRuntimeClient _containerRuntime;
    private readonly IWorkflowOperationStore? _workflowStore;
    private readonly SelfHostedDeployOptions _options;
    private readonly object _sync = new();
    private string _activeAddress;

    public YarpInMemoryProxyStateSwapper(
        InMemoryConfigProvider configProvider,
        IContainerRuntimeClient containerRuntime,
        Microsoft.Extensions.Options.IOptions<SelfHostedDeployOptions> options,
        IWorkflowOperationStore? workflowStore = null)
    {
        _configProvider = configProvider;
        _containerRuntime = containerRuntime;
        _options = options.Value;
        _workflowStore = workflowStore;
        _activeAddress = InitialActiveAddress(_options);
    }

    public bool IsConfigured => true;

    public string? ActiveDestinationAddress
    {
        get
        {
            lock (_sync)
            {
                return _activeAddress;
            }
        }
    }

    public Task SwapAsync(string destinationAddress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationAddress);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _configProvider.Update(
                SelfHostedProxyConfig.BuildRoutes(_options),
                SelfHostedProxyConfig.BuildClusters(_options, destinationAddress));
            _activeAddress = destinationAddress;
        }

        return Task.CompletedTask;
    }

    public async Task ReconcileFromRuntimeAsync(CancellationToken cancellationToken)
    {
        var containers = await _containerRuntime
            .ListAsync(_options.ContainerRuntime, NoLabelSelectors, cancellationToken)
            .ConfigureAwait(false);

        var activeDeployOperations = _workflowStore != null
            ? await _workflowStore.ListActiveAsync(WorkflowOperationKind.Deploy, cancellationToken).ConfigureAwait(false)
            : null;

        var destination = SelfHostedProxyReconstruction.ResolveRunningDestination(containers, _options, activeDeployOperations);
        if (string.IsNullOrEmpty(destination) || string.Equals(destination, ActiveDestinationAddress, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await SwapAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    internal static string InitialActiveAddress(SelfHostedDeployOptions options)
        => $"http://{options.Host}:{options.ActivePort.ToString(CultureInfo.InvariantCulture)}/";
}

internal sealed partial class SelfHostedProxyReconciliationService(
    YarpInMemoryProxyStateSwapper swapper,
    ILogger<SelfHostedProxyReconciliationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await swapper.ReconcileFromRuntimeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a reconciliation failure must not block startup. The configured active
            // destination remains in place and the next deploy operation re-establishes ground truth.
            Log.ReconciliationFailed(logger, ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static partial class Log
    {
        [LoggerMessage(9332, LogLevel.Warning, "Self-hosted proxy startup reconciliation failed: {Reason}")]
        public static partial void ReconciliationFailed(ILogger logger, string reason);
    }
}

internal sealed class DisabledProxyStateSwapper : IProxyStateSwapper
{
    public bool IsConfigured => false;

    public string? ActiveDestinationAddress => null;

    public Task SwapAsync(string destinationAddress, CancellationToken cancellationToken)
        => throw new InvalidOperationException(
            "The embedded reverse proxy is not enabled; set ControlPlane:SelfHosted:Enabled=true to use the self-hosted rolling deploy backend.");
}

internal static class SelfHostedProxyConfig
{
    public static IReadOnlyList<RouteConfig> BuildRoutes(SelfHostedDeployOptions options)
        =>
        [
            new RouteConfig
            {
                RouteId = options.RouteId,
                ClusterId = options.ClusterId,
                Match = new RouteMatch { Path = options.RoutePath }
            }
        ];

    public static IReadOnlyList<ClusterConfig> BuildClusters(SelfHostedDeployOptions options, string destinationAddress)
        =>
        [
            new ClusterConfig
            {
                ClusterId = options.ClusterId,
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
                {
                    ["active"] = new DestinationConfig { Address = destinationAddress }
                }
            }
        ];
}

// This local probe skips the outbound address guard because the target is a sibling replica, and the URL is built from validated ports.
internal sealed partial class HttpLocalReplicaHealthProbe(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpLocalReplicaHealthProbe>? logger = null) : ILocalReplicaHealthProbe
{
    private const int MinimumSamples = 1;
    private const int MaximumSamples = 20;

    public async Task<LocalReplicaHealthResult> ProbeAsync(
        string url,
        int samples,
        int timeoutSeconds,
        int expectedStatusCode,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new LocalReplicaHealthResult { Attempts = 0, Failures = 0, Detail = "Replica health URL is not a valid absolute URL." };
        }

        var clampedSamples = Math.Clamp(samples <= 0 ? 1 : samples, MinimumSamples, MaximumSamples);
        var timeout = Math.Max(1, timeoutSeconds);
        var client = httpClientFactory.CreateClient(ControlPlaneHttpClients.Probe);
        var failures = 0;
        var reached = false;
        string? body = null;

        for (var attempt = 0; attempt < clampedSamples; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);
                reached = true;
                if ((int)response.StatusCode != expectedStatusCode)
                {
                    failures++;
                    continue;
                }

                body ??= await ReadProbeBodyAsync(response, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional broad catch: per-attempt loop probing replica health; one
                // probe's failure is counted and the loop continues to the next attempt
                // rather than aborting the health check.
                failures++;
                if (logger is not null)
                {
                    Log.ProbeAttemptFailed(logger, url, ex.Message);
                }
            }
        }

        return new LocalReplicaHealthResult
        {
            Attempts = clampedSamples,
            Failures = failures,
            Reached = reached,
            Body = body,
            Detail = $"{failures} of {clampedSamples} replica health checks did not return {expectedStatusCode.ToString(CultureInfo.InvariantCulture)}."
        };
    }

    private static async Task<string?> ReadProbeBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int limit = 65_536;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var rented = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(rented.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > limit)
            {
                return null;
            }

            buffer.Write(rented, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static partial class Log
    {
        [LoggerMessage(9333, LogLevel.Debug,
            "Replica health probe attempt for {Url} failed: {Reason}")]
        public static partial void ProbeAttemptFailed(ILogger logger, string url, string reason);
    }
}

internal sealed partial class ProcessContainerRuntimeClient(ILogger<ProcessContainerRuntimeClient> logger)
    : IContainerRuntimeClient
{
    public async Task<bool> IsAvailableAsync(string executable, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        try
        {
            var (exitCode, _, _) = await RunProcessAsync(
                executable, ["version", "--format", "{{.Server.Version}}"], throwIfStartFails: false, cancellationToken).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.RuntimeProbeFailed(logger, executable, ex.Message);
            return false;
        }
    }

    public async Task<string> RunAsync(ContainerRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var arguments = new List<string> { "run", "-d", "--name", request.ContainerName };
        foreach (var label in request.Labels)
        {
            arguments.Add("--label");
            arguments.Add($"{label.Key}={label.Value}");
        }

        foreach (var variable in request.Environment)
        {
            arguments.Add("-e");
            arguments.Add($"{variable.Key}={variable.Value}");
        }

        // Read-only so a replica can never write back to operator-supplied material (#4617).
        foreach (var mount in request.Mounts)
        {
            arguments.Add("-v");
            arguments.Add($"{mount.HostPath}:{mount.ContainerPath}:ro");
        }

        arguments.Add("-p");
        arguments.Add($"{request.HostPort.ToString(CultureInfo.InvariantCulture)}:{request.ContainerPort.ToString(CultureInfo.InvariantCulture)}");
        arguments.Add(request.Image);

        var (exitCode, stdout, _) = await RunProcessAsync(
            request.Executable, arguments, throwIfStartFails: true, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Container runtime '{request.Executable}' failed to run '{request.ContainerName}' (exit {exitCode.ToString(CultureInfo.InvariantCulture)}).");
        }

        var containerId = stdout.Trim();
        return string.IsNullOrWhiteSpace(containerId) ? request.ContainerName : containerId;
    }

    public async Task StopAsync(string executable, string containerNameOrId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerNameOrId);

        // rm -f stops and removes so the deterministic name is reusable on the next rollout. Best-effort:
        // a container that is already gone is not an error for the cutover/rollback lifecycle.
        var (exitCode, _, stderr) = await RunProcessAsync(
            executable, ["rm", "-f", containerNameOrId], throwIfStartFails: false, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            Log.StopFailed(logger, containerNameOrId, stderr.Trim());
        }
    }

    public async Task<IReadOnlyList<ContainerSummary>> ListAsync(
        string executable,
        IReadOnlyDictionary<string, string> labelSelectors,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        var arguments = new List<string> { "ps", "-a" };
        foreach (var selector in labelSelectors)
        {
            arguments.Add("--filter");
            arguments.Add($"label={selector.Key}={selector.Value}");
        }

        arguments.Add("--format");
        arguments.Add("{{.ID}}\t{{.Names}}\t{{.State}}\t{{.Labels}}");

        var (exitCode, stdout, _) = await RunProcessAsync(
            executable, arguments, throwIfStartFails: false, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return Array.Empty<ContainerSummary>();
        }

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length >= 4)
            .Select(columns => new ContainerSummary
            {
                Id = columns[0],
                Name = columns[1],
                Running = string.Equals(columns[2], "running", StringComparison.OrdinalIgnoreCase),
                Labels = ParseLabels(columns[3])
            })
            .ToList();
    }

    private static Dictionary<string, string> ParseLabels(string raw)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                labels[pair[..separator]] = pair[(separator + 1)..];
            }
        }

        return labels;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        bool throwIfStartFails,
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

        if (!process.Start())
        {
            if (throwIfStartFails)
            {
                throw new InvalidOperationException($"Failed to start container runtime '{executable}'.");
            }

            return (-1, string.Empty, string.Empty);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Best-effort: the process may have exited in the race between the
            // HasExited check and Kill(), which is not an error for this cleanup path.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best-effort: platform-specific failure to signal the process (e.g. it is
            // already terminating); nothing actionable to do beyond letting exit proceed.
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9330, LogLevel.Debug, "Container runtime probe for {Executable} failed: {Reason}")]
        public static partial void RuntimeProbeFailed(ILogger logger, string executable, string reason);

        [LoggerMessage(9331, LogLevel.Warning, "Failed to stop container {ContainerNameOrId}: {Reason}")]
        public static partial void StopFailed(ILogger logger, string containerNameOrId, string reason);
    }
}

internal static class SelfHostedRollingProxyRegistration
{
    public static IServiceCollection AddHonuaSelfHostedRollingProxy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SelfHostedDeployOptions>()
            .Bind(configuration.GetSection(SelfHostedDeployOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<SelfHostedDeployOptions>, SelfHostedDeployOptionsValidator>();

        // Seams the rolling deploy backend consumes are always registered so the unconditionally-
        // registered backend resolves; only the actual proxy wiring is gated on Enabled.
        services.AddSingleton<IContainerRuntimeClient, ProcessContainerRuntimeClient>();
        services.AddSingleton<ILocalReplicaHealthProbe, HttpLocalReplicaHealthProbe>();

        var options = new SelfHostedDeployOptions();
        configuration.GetSection(SelfHostedDeployOptions.SectionName).Bind(options);

        if (options.Enabled)
        {
            var initialDestination = YarpInMemoryProxyStateSwapper.InitialActiveAddress(options);
            services
                .AddReverseProxy()
                .LoadFromMemory(
                    SelfHostedProxyConfig.BuildRoutes(options),
                    SelfHostedProxyConfig.BuildClusters(options, initialDestination));
            services.AddSingleton<YarpInMemoryProxyStateSwapper>();
            services.AddSingleton<IProxyStateSwapper>(sp => sp.GetRequiredService<YarpInMemoryProxyStateSwapper>());

            // Restore the proxy destination from container ground truth at startup so a post-promotion
            // restart cannot keep forwarding to a deleted active replica.
            services.AddHostedService<SelfHostedProxyReconciliationService>();
        }
        else
        {
            services.AddSingleton<IProxyStateSwapper, DisabledProxyStateSwapper>();
        }

        return services;
    }

    public static bool IsSelfHostedProxyEnabled(IConfiguration configuration)
        => configuration.GetSection(SelfHostedDeployOptions.SectionName).GetValue<bool>("Enabled");
}
