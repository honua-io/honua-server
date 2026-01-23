// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Domain;
using Xunit.Sdk;

namespace Honua.Server.Tests.Helpers;

internal static class LocalstackCli
{
    private const string AwslocalCommand = "awslocal";
    private const string LocalstackCommand = "localstack";

    internal static async Task<JsonDocument> HeadObjectAsync(AwsS3Options options, string objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var endpointUrl = options.ServiceUrl;
        var args = new List<string>
        {
            "s3api",
            "head-object",
            "--bucket",
            options.BucketName,
            "--key",
            objectKey
        };

        if (!string.IsNullOrWhiteSpace(endpointUrl))
        {
            args.Add("--endpoint-url");
            args.Add(endpointUrl);
        }

        var output = await RunAwsCommandAsync(options, args, cancellationToken);
        return JsonDocument.Parse(output);
    }

    private static async Task<string> RunAwsCommandAsync(
        AwsS3Options options,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var (command, useLocalstackWrapper) = ResolveCommandOrSkip();
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (useLocalstackWrapper)
        {
            startInfo.ArgumentList.Add("aws");
        }

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(options.AccessKeyId))
        {
            startInfo.Environment["AWS_ACCESS_KEY_ID"] = options.AccessKeyId;
        }

        if (!string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            startInfo.Environment["AWS_SECRET_ACCESS_KEY"] = options.SecretAccessKey;
        }

        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            startInfo.Environment["AWS_DEFAULT_REGION"] = options.Region;
            startInfo.Environment["AWS_REGION"] = options.Region;
        }

        startInfo.Environment["AWS_EC2_METADATA_DISABLED"] = "true";
        startInfo.Environment["AWS_PAGER"] = string.Empty;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {command}.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            var message = new StringBuilder()
                .AppendLine("Localstack CLI command failed.")
                .AppendLine("Command: " + command + " " + string.Join(" ", startInfo.ArgumentList))
                .AppendLine("Exit code: " + process.ExitCode)
                .AppendLine("Stdout:")
                .AppendLine(output)
                .AppendLine("Stderr:")
                .AppendLine(error)
                .ToString();
            throw new InvalidOperationException(message);
        }

        return output;
    }

    private static (string Command, bool UseLocalstackWrapper) ResolveCommandOrSkip()
    {
        var awslocal = FindOnPath(AwslocalCommand);
        if (!string.IsNullOrWhiteSpace(awslocal))
        {
            return (awslocal, false);
        }

        var localstack = FindOnPath(LocalstackCommand);
        if (!string.IsNullOrWhiteSpace(localstack))
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOCALSTACK_AUTH_TOKEN")))
            {
                throw SkipException.ForSkip("Localstack CLI requires LOCALSTACK_AUTH_TOKEN. Install awscli-local for awslocal.");
            }

            return (localstack, true);
        }

        throw SkipException.ForSkip("Localstack CLI (awslocal or localstack) not found on PATH.");
    }

    private static string? FindOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var commandName = OperatingSystem.IsWindows() ? $"{command}.exe" : command;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry.Trim(), commandName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
