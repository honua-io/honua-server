// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Production <see cref="IGdalCommandRunner"/> that invokes the GDAL/OGR CLI
/// tools as child processes. The tools are provided by the GDAL base image the
/// worker container is built from; this runner does not bundle managed GDAL
/// bindings so it adds no native package dependencies to any managed assembly.
/// </summary>
internal sealed partial class ProcessGdalCommandRunner(
    IOptions<GdalHardeningOptions> hardening,
    IOptions<AwsS3Options> s3Options,
    IOptions<AzureBlobOptions> azureOptions,
    ILogger<ProcessGdalCommandRunner> logger,
    IConfiguration? configuration = null) : IGdalCommandRunner
{
    private static readonly string[] InheritedCloudCredentialVariables =
    [
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SESSION_TOKEN",
        "AWS_PROFILE",
        "AWS_WEB_IDENTITY_TOKEN_FILE",
        "AWS_ROLE_ARN",
        "AWS_CONTAINER_CREDENTIALS_FULL_URI",
        "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
        "AZURE_STORAGE_CONNECTION_STRING",
        "AZURE_STORAGE_ACCOUNT",
        "AZURE_STORAGE_ACCESS_KEY",
        "AZURE_STORAGE_SAS_TOKEN",
        "AZURE_CLIENT_ID",
        "AZURE_CLIENT_SECRET",
        "AZURE_TENANT_ID"
    ];

    /// <inheritdoc />
    public async Task<GdalCommandResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(arguments);

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

        // Harden every GDAL subprocess (#2765): skip the indirection/network drivers
        // (VRT, WMS, …) and — for a pure local-scratch invocation — neutralize the
        // remote virtual-filesystem handlers. Applied by overwriting the inherited
        // environment so a value set on the worker process cannot weaken the policy.
        var referencesRemoteVsi = GdalRuntimeHardening.ArgumentsReferenceVsi(arguments);
        var hardeningEnv = GdalRuntimeHardening.BuildEnvironment(
            hardening.Value,
            referencesRemoteVsi,
            s3Options.Value,
            azureOptions.Value,
            GdalRuntimeHardening.ArgumentsReferenceS3Vsi(arguments),
            GdalRuntimeHardening.ArgumentsReferenceAzureVsi(arguments));
        ApplyHardenedEnvironment(
            startInfo.Environment,
            hardeningEnv,
            s3Options.Value,
            azureOptions.Value,
            configuration);

        Log.RunningTool(logger, tool, string.Join(' ', arguments));

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
            throw new InvalidOperationException($"Failed to start GDAL tool '{tool}'.");
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

        // Ensure async stream readers flush before the buffers are read.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        var result = new GdalCommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString()
        };

        Log.ToolCompleted(logger, tool, result.ExitCode);
        return result;
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

        var sensitiveNames = new HashSet<string>(InheritedCloudCredentialVariables, StringComparer.OrdinalIgnoreCase);
        AddEnvironmentReference(sensitiveNames, s3Options.AccessKeyId);
        AddEnvironmentReference(sensitiveNames, s3Options.SecretAccessKey);
        AddEnvironmentReference(sensitiveNames, azureOptions.ConnectionString);
        if (configuration is not null)
        {
            AddEnvironmentReference(sensitiveNames, configuration["FileStorage:AwsS3:AccessKeyId"]);
            AddEnvironmentReference(sensitiveNames, configuration["FileStorage:AwsS3:SecretAccessKey"]);
            AddEnvironmentReference(sensitiveNames, configuration["FileStorage:AzureBlob:ConnectionString"]);
        }

        // ProcessStartInfo starts with a copy of the worker's complete environment. Strip every
        // known cloud credential and configured env: indirection before re-adding only the values
        // BuildEnvironment authorized for this exact S3/Azure VSI invocation.
        foreach (var inheritedName in environment.Keys.Where(sensitiveNames.Contains).ToArray())
        {
            environment.Remove(inheritedName);
        }

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
            // Process already exited between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Permission/race killing the process; nothing more we can do.
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
