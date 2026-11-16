// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Worker.Gdal.Execution;

namespace Honua.Worker.Gdal.Tests;

internal sealed class FakeGdalCommandRunner : IGdalCommandRunner
{
    private readonly Func<string, IReadOnlyList<string>, string, GdalCommandResult> _behavior;

    public FakeGdalCommandRunner(Func<string, IReadOnlyList<string>, string, GdalCommandResult> behavior)
    {
        _behavior = behavior;
    }

    public List<(string Tool, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

    public Task<GdalCommandResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        Invocations.Add((tool, arguments));
        return Task.FromResult(_behavior(tool, arguments, workingDirectory));
    }

    public static FakeGdalCommandRunner Succeeding(byte[] outputBytes, int outputArgIndexFromEnd = 0)
        => new((_, args, _) =>
        {
            var outputPath = args[^(outputArgIndexFromEnd + 1)];
            File.WriteAllBytes(outputPath, outputBytes);
            return new GdalCommandResult { ExitCode = 0 };
        });

    public static FakeGdalCommandRunner Failing(int exitCode, string stderr)
        => new((_, _, _) => new GdalCommandResult { ExitCode = exitCode, StandardError = stderr });
}
