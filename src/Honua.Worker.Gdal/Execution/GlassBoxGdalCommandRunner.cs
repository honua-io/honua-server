// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Dev-only decorator over an inner <see cref="IGdalCommandRunner"/> that records every
/// invocation — the real (unredacted) command line, the real scratch working directory,
/// and the FULL stdout/stderr — into a <see cref="GlassBoxCapture"/> for the GP Devkit
/// "glass box" (issue #2128).
/// </summary>
/// <remarks>
/// <para>
/// This runner is the ONLY place the unsanitized native command and full process output
/// are surfaced, and it is wired exclusively by the dev <c>honua gp</c> CLI's glass-box
/// mode (via <c>GdalWorkerServiceCollectionExtensions.AddGlassBoxGdalCapture</c>). The
/// production worker host registers the bare <see cref="ProcessGdalCommandRunner"/> and
/// never this decorator, so the sanitized <see cref="GdalErrorSanitizer"/> /
/// <see cref="GdalCommandLog"/> path remains the default and clients never see raw paths
/// or untruncated stderr.
/// </para>
/// <para>
/// The decorator is transparent: it returns the inner result verbatim so the executor's
/// own sanitized logging and error classification are completely unchanged. It only
/// observes — it never mutates the inner runner's behavior.
/// </para>
/// </remarks>
internal sealed class GlassBoxGdalCommandRunner(IGdalCommandRunner inner, GlassBoxCapture capture) : IGdalCommandRunner
{
    private readonly IGdalCommandRunner _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly GlassBoxCapture _capture = capture ?? throw new ArgumentNullException(nameof(capture));

    /// <inheritdoc />
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
