// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Worker.Gdal.Execution;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Fake <see cref="IGdalCommandRunner"/> that records invocations and, on a
/// configured success, writes a caller-supplied output payload to the output
/// path argument so the executor's read-back / artifact path can be exercised
/// without GDAL installed.
/// </summary>
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

    /// <summary>
    /// Builds a runner that succeeds and writes <paramref name="outputBytes"/> to
    /// the last argument (the output path, by GDAL CLI convention for ogr2ogr;
    /// the second-to-last for gdalwarp, so callers pass the matching output index).
    /// </summary>
    public static FakeGdalCommandRunner Succeeding(byte[] outputBytes, int outputArgIndexFromEnd = 0)
        => new((_, args, _) =>
        {
            var outputPath = args[^(outputArgIndexFromEnd + 1)];
            File.WriteAllBytes(outputPath, outputBytes);
            return new GdalCommandResult { ExitCode = 0 };
        });

    /// <summary>
    /// Builds a runner that fails with the supplied exit code and stderr.
    /// </summary>
    public static FakeGdalCommandRunner Failing(int exitCode, string stderr)
        => new((_, _, _) => new GdalCommandResult { ExitCode = exitCode, StandardError = stderr });
}
