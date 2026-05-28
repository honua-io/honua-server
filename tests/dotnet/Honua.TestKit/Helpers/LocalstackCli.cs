// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.TestKit.Helpers;

public static class LocalstackCli
{
    private const string AwslocalCommand = "awslocal";
    private const string LocalstackCommand = "localstack";

    public static async Task<JsonDocument> HeadObjectAsync(AwsS3Options options, string objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        if (!TryResolveCommand(out var command, out var useLocalstackWrapper))
        {
            return await HeadObjectWithSdkAsync(options, objectKey, cancellationToken);
        }

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

        var output = await RunAwsCommandAsync(command!, useLocalstackWrapper, options, args, cancellationToken);
        return JsonDocument.Parse(output);
    }

    private static async Task<string> RunAwsCommandAsync(
        string command,
        bool useLocalstackWrapper,
        AwsS3Options options,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
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

    private static bool TryResolveCommand(out string? command, out bool useLocalstackWrapper)
    {
        var awslocal = FindOnPath(AwslocalCommand);
        if (!string.IsNullOrWhiteSpace(awslocal))
        {
            command = awslocal;
            useLocalstackWrapper = false;
            return true;
        }

        var localstack = FindOnPath(LocalstackCommand);
        if (!string.IsNullOrWhiteSpace(localstack))
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOCALSTACK_AUTH_TOKEN")))
            {
                command = null;
                useLocalstackWrapper = false;
                return false;
            }

            command = localstack;
            useLocalstackWrapper = true;
            return true;
        }

        command = null;
        useLocalstackWrapper = false;
        return false;
    }

    private static async Task<JsonDocument> HeadObjectWithSdkAsync(
        AwsS3Options options,
        string objectKey,
        CancellationToken cancellationToken)
    {
        using var client = CreateS3Client(options);
        var response = await client.GetObjectMetadataAsync(
            new GetObjectMetadataRequest
            {
                BucketName = options.BucketName,
                Key = objectKey
            },
            cancellationToken);

        var metadata = NormalizeMetadata(response.Metadata);
        var payload = JsonSerializer.Serialize(new { Metadata = metadata });
        return JsonDocument.Parse(payload);
    }

    private static AmazonS3Client CreateS3Client(AwsS3Options options)
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region),
            ForcePathStyle = options.ForcePathStyle
        };

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }

        if (!string.IsNullOrWhiteSpace(options.AccessKeyId) &&
            !string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            return new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
                config);
        }

        return new AmazonS3Client(config);
    }

    private static Dictionary<string, string> NormalizeMetadata(MetadataCollection metadata)
    {
        const string metadataPrefix = "x-amz-meta-";
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in metadata.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = metadata[key];
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var normalizedKey = key.StartsWith(metadataPrefix, StringComparison.OrdinalIgnoreCase)
                ? key[metadataPrefix.Length..]
                : key;

            normalized[normalizedKey] = value;
        }

        return normalized;
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
