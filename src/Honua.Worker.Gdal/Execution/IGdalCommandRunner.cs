// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Result of running a GDAL/OGR command-line tool.
/// </summary>
internal sealed record GdalCommandResult
{
    /// <summary>
    /// Process exit code. Zero indicates success for every GDAL CLI tool.
    /// </summary>
    public required int ExitCode { get; init; }

    /// <summary>
    /// Captured standard output.
    /// </summary>
    public string StandardOutput { get; init; } = "";

    /// <summary>
    /// Captured standard error. GDAL tools write diagnostics here even on success.
    /// </summary>
    public string StandardError { get; init; } = "";

    /// <summary>
    /// Whether the command completed successfully (exit code zero).
    /// </summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Abstraction over invoking a GDAL/OGR command-line tool (for example
/// <c>gdalwarp</c> or <c>ogr2ogr</c>) as a child process.
/// </summary>
/// <remarks>
/// The heavyweight GDAL worker image ships the GDAL CLI binaries on the
/// <c>ghcr.io/osgeo/gdal</c> base; the production implementation
/// (<see cref="ProcessGdalCommandRunner"/>) shells out to them. The seam exists
/// so worker-side executor logic — input projection, artifact size guards,
/// canonical result shaping — can be unit-tested with a fake runner on a host
/// that does not have GDAL installed, while the same executor code path runs
/// the real tools inside the worker image.
/// </remarks>
internal interface IGdalCommandRunner
{
    /// <summary>
    /// Runs the named GDAL/OGR tool with the supplied arguments.
    /// </summary>
    /// <param name="tool">Executable name, e.g. <c>gdalwarp</c> or <c>ogr2ogr</c>.</param>
    /// <param name="arguments">Ordered argument list passed verbatim to the tool.</param>
    /// <param name="workingDirectory">Working directory for the child process.</param>
    /// <param name="cancellationToken">Cancellation token; kills the process when triggered.</param>
    Task<GdalCommandResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}
