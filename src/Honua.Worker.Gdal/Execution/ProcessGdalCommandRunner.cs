// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Subprocesses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

internal sealed partial class ProcessGdalCommandRunner(
    IOptions<GdalHardeningOptions> hardening,
    IOptions<AwsS3Options> s3Options,
    IOptions<AzureBlobOptions> azureOptions,
    ILogger<ProcessGdalCommandRunner> logger,
    IConfiguration? configuration = null) : IGdalCommandRunner
{
    public async Task<GdalCommandResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        GdalErrorSanitizer.UseConfiguredEndpoints(s3Options.Value, azureOptions.Value);

        var startInfo = new ProcessStartInfo
        {
            FileName = tool,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Harden every GDAL subprocess: skip the indirection/network drivers
        // (VRT, WMS, …) and — for a pure local-scratch invocation — neutralize the
        // remote virtual-filesystem handlers. Applied by overwriting the inherited
        // environment so a value set on the worker process cannot weaken the policy.
        var remoteInputs = GdalRemoteInputScope.Classify(arguments);
        var hardeningEnv = GdalRuntimeHardening.BuildEnvironment(
            hardening.Value,
            remoteInputs.Remote,
            s3Options.Value,
            azureOptions.Value,
            remoteInputs.S3,
            remoteInputs.Azure);
        ApplyHardenedEnvironment(
            startInfo.Environment,
            hardeningEnv,
            s3Options.Value,
            azureOptions.Value,
            configuration);

        var nativeEntry = NativeEntryQualification.Prepare(startInfo);
        Log.RunningTool(logger, tool, string.Join(' ', arguments));

        using var process = new Process { StartInfo = startInfo };
        var stdout = BoundedProcessOutputBuffer.CreateWithoutTruncationMarker(
            BoundedProcessOutputBuffer.DefaultStandardOutputMaxChars);
        var stderr = BoundedProcessOutputBuffer.CreateWithTruncationMarker();

        process.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);

        await RunProcessAsync(process, nativeEntry, cancellationToken).ConfigureAwait(false);

        // Ensure async stream readers flush before the buffers are read.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        var result = new GdalCommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            StandardOutputTruncated = stdout.Truncated
        };

        Log.ToolCompleted(logger, tool, result.ExitCode);
        return result;
    }

    internal static async Task RunProcessAsync(
        Process process, NativeEntryQualification? nativeEntry, CancellationToken cancellationToken)
    {
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the GDAL tool.");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (nativeEntry is not null)
            {
                await nativeEntry.WaitAsync(process, cancellationToken).ConfigureAwait(false);
            }
            await ExecutionQualificationBarrier.WaitAsync(
                "native-process-started", cancellationToken, process.Id).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            nativeEntry?.Release();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Any launch/reader/handshake/IO failure must kill a child held before main,
            // not just operator cancellation. Reap is bounded and preserves the original error.
            SubprocessEnvironmentScrubber.TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                // Independent observation still refuses a surviving/unreaped native child.
            }
            throw;
        }
    }

    internal static void ApplyHardenedEnvironment(
        IDictionary<string, string?> environment,
        IReadOnlyDictionary<string, string> hardeningEnvironment,
        AwsS3Options s3Options,
        AzureBlobOptions azureOptions,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(hardeningEnvironment);
        ArgumentNullException.ThrowIfNull(s3Options);
        ArgumentNullException.ThrowIfNull(azureOptions);

        var additionalSensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddEnvironmentReference(additionalSensitiveNames, s3Options.AccessKeyId);
        AddEnvironmentReference(additionalSensitiveNames, s3Options.SecretAccessKey);
        AddEnvironmentReference(additionalSensitiveNames, azureOptions.ConnectionString);
        if (configuration is not null)
        {
            AddEnvironmentReference(additionalSensitiveNames, configuration["FileStorage:AwsS3:AccessKeyId"]);
            AddEnvironmentReference(additionalSensitiveNames, configuration["FileStorage:AwsS3:SecretAccessKey"]);
            AddEnvironmentReference(additionalSensitiveNames, configuration["FileStorage:AzureBlob:ConnectionString"]);
        }

        // Strip inherited cloud credentials, configured secret references, and host secrets
        // before re-adding only the values BuildEnvironment authorized for this invocation.
        SubprocessEnvironmentScrubber.Scrub(environment, additionalSensitiveNames);

        foreach (var kvp in hardeningEnvironment)
        {
            environment[kvp.Key] = kvp.Value;
        }
    }

    private static void AddEnvironmentReference(HashSet<string> sensitiveNames, string? configuredValue)
    {
        const string prefix = "env:";
        if (configuredValue is not null && configuredValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = configuredValue[prefix.Length..].Trim();
            if (name.Length > 0)
            {
                sensitiveNames.Add(name);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9200, LogLevel.Information, "Running GDAL tool {Tool} {Arguments}")]
        public static partial void RunningTool(ILogger logger, string tool, string arguments);

        [LoggerMessage(9201, LogLevel.Information, "GDAL tool {Tool} exited with code {ExitCode}")]
        public static partial void ToolCompleted(ILogger logger, string tool, int exitCode);
    }
}

internal sealed class BoundedProcessOutputBuffer
{
    internal const int DefaultMaxChars = 256 * 1024;
    internal const int DefaultStandardOutputMaxChars = 50 * 1024 * 1024;
    internal const string TruncationMarker = "... [output truncated]";

    private enum TruncationMarkerMode
    {
        Append,
        Omit,
    }

    private readonly int _maxChars;
    private readonly TruncationMarkerMode _truncationMarkerMode;
    private readonly Lock _sync = new();
    private readonly StringBuilder _builder = new();
    private bool _truncated;

    public BoundedProcessOutputBuffer(int maxChars = DefaultMaxChars)
        : this(maxChars, TruncationMarkerMode.Append)
    {
    }

    private BoundedProcessOutputBuffer(int maxChars, TruncationMarkerMode truncationMarkerMode)
    {
        _maxChars = maxChars > 0
            ? maxChars
            : throw new ArgumentOutOfRangeException(nameof(maxChars), "Maximum character limit must be positive.");
        _truncationMarkerMode = truncationMarkerMode;
    }

    public static BoundedProcessOutputBuffer CreateWithTruncationMarker(int maxChars = DefaultMaxChars)
        => new(maxChars, TruncationMarkerMode.Append);

    public static BoundedProcessOutputBuffer CreateWithoutTruncationMarker(int maxChars = DefaultMaxChars)
        => new(maxChars, TruncationMarkerMode.Omit);

    public bool Truncated
    {
        get
        {
            lock (_sync)
            {
                return _truncated;
            }
        }
    }

    public void AppendLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_sync)
        {
            if (_truncated)
            {
                return;
            }

            var remaining = _maxChars - _builder.Length;
            var lineLengthWithNewLine = line.Length + Environment.NewLine.Length;
            if (lineLengthWithNewLine <= remaining)
            {
                _builder.AppendLine(line);
                return;
            }

            if (remaining > 0)
            {
                _builder.Append(line, 0, Math.Min(line.Length, remaining));
            }

            if (_truncationMarkerMode == TruncationMarkerMode.Append)
            {
                _builder.AppendLine();
                _builder.Append(TruncationMarker);
            }

            _truncated = true;
        }
    }

    public override string ToString()
    {
        lock (_sync)
        {
            return _builder.ToString();
        }
    }
}
