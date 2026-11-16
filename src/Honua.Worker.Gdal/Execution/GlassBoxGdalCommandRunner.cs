// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Worker.Gdal.Execution;

internal sealed class GlassBoxGdalCommandRunner(IGdalCommandRunner inner, GlassBoxCapture capture) : IGdalCommandRunner
{
    private readonly IGdalCommandRunner _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly GlassBoxCapture _capture = capture ?? throw new ArgumentNullException(nameof(capture));

    public async Task<GdalCommandResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await _inner.RunAsync(tool, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);

        _capture.Record(new GlassBoxCommand
        {
            Tool = tool,
            CommandLine = GlassBoxCapture.BuildCommandLine(tool, arguments),
            WorkingDirectory = workingDirectory,
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
        });

        return result;
    }
}
