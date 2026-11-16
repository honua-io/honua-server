// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

internal sealed partial class DockerGdalCommandRunner(
    IDockerCommandInvoker invoker,
    IOptions<GdalContainerExecutionOptions> options,
    IOptions<GdalHardeningOptions> hardening,
    IOptions<AwsS3Options> s3Options,
    IOptions<AzureBlobOptions> azureOptions,
    ILogger<DockerGdalCommandRunner> logger) : IGdalCommandRunner
{
    public Task<GdalCommandResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        GdalErrorSanitizer.UseConfiguredEndpoints(s3Options.Value, azureOptions.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var opts = options.Value;
        var remoteInputs = GdalRemoteInputScope.Classify(arguments);
        var hardeningEnv = GdalRuntimeHardening.BuildEnvironment(
            hardening.Value,
            remoteInputs.Remote,
            s3Options.Value,
            azureOptions.Value,
            remoteInputs.S3,
            remoteInputs.Azure);
        var referencesRemoteVsi = remoteInputs.Remote;
        var network = referencesRemoteVsi ? opts.RemoteVsiNetwork : opts.Network;
        if (referencesRemoteVsi
            && (string.IsNullOrWhiteSpace(network)
                || string.Equals(network, "none", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "GdalContainer:RemoteVsiNetwork must enable networking for trusted remote VSI inputs.");
        }

        var dockerArgs = BuildDockerRunArguments(
            opts,
            tool,
            arguments,
            workingDirectory,
            hardeningEnv,
            network);

        Log.DispatchingToContainer(logger, tool, opts.Image, workingDirectory);
        return invoker.RunAsync(opts.DockerExecutable, dockerArgs, hardeningEnv, cancellationToken);
    }

    public static IReadOnlyList<string> BuildDockerRunArguments(
        GdalContainerExecutionOptions options,
        string tool,
        IReadOnlyList<string> toolArguments,
        string workspace,
        IReadOnlyDictionary<string, string>? environment = null,
        string? network = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(toolArguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        var envArgs = environment is null
            ? []
            : GdalRuntimeHardening.ToDockerEnvArguments(environment);

        var args = new List<string>(toolArguments.Count + envArgs.Count + 14)
        {
            "run",
            "--rm",
        };

        var effectiveNetwork = network ?? options.Network;
        if (!string.IsNullOrWhiteSpace(effectiveNetwork))
        {
            args.Add("--network");
            args.Add(effectiveNetwork);
        }

        if (!string.IsNullOrWhiteSpace(options.User))
        {
            args.Add("--user");
            args.Add(options.User);
        }

        // Propagate the GDAL environment by NAME. The container-runtime process
        // receives the values out of band, so credentials never enter argv/logs.
        args.AddRange(envArgs);

        // Identical-path bind mount: the executor's absolute workspace paths must
        // resolve to the same files inside the container.
        args.Add("-v");
        args.Add($"{workspace}:{workspace}");
        args.Add("-w");
        args.Add(workspace);

        // Override the image entrypoint (dotnet Honua.Worker.Gdal.dll) so the raw
        // GDAL tool on the image PATH is what runs.
        args.Add("--entrypoint");
        args.Add(tool);

        args.Add(options.Image);
        args.AddRange(toolArguments);

        return args;
    }

    private static partial class Log
    {
        [LoggerMessage(9273, LogLevel.Information,
            "Dispatching GDAL tool {Tool} to container image {Image} (workspace {Workspace})")]
        public static partial void DispatchingToContainer(ILogger logger, string tool, string image, string workspace);
    }
}
