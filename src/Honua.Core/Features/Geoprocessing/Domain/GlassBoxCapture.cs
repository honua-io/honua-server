// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// A single, fully UNSANITIZED record of a native GDAL/OGR child-process invocation,
/// captured by the GP Devkit dev-mode "glass box" (issue #2128). Unlike the structured
/// <c>gdal.command</c> log entry — which redacts the per-job scratch workspace to a
/// <c>&lt;scratch&gt;</c> placeholder for the client-visible, production-safe path — a
/// glass-box record preserves the REAL scratch paths and the FULL stdout/stderr so a
/// developer can inspect intermediate files and re-run the exact command locally.
/// </summary>
/// <remarks>
/// These records are only ever populated when a caller explicitly opts a run into
/// glass-box mode (a dev-only flag/env on <c>honua gp</c>); the production worker
/// host never wires the capturing runner, so the sanitized path stays the default.
/// </remarks>
public sealed record GlassBoxCommand
{
    /// <summary>The GDAL/OGR tool name (e.g. <c>ogr2ogr</c>, <c>gdalwarp</c>).</summary>
    public required string Tool { get; init; }

    /// <summary>
    /// The exact, unredacted command line (tool plus arguments, shell-quoted) a
    /// developer can paste to re-run the operation against the intermediate files.
    /// </summary>
    public required string CommandLine { get; init; }

    /// <summary>The real (unredacted) scratch working directory the tool ran in.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The process exit code (zero is success for every GDAL CLI tool).</summary>
    public required int ExitCode { get; init; }

    /// <summary>Full, untruncated standard output captured from the child process.</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Full, untruncated standard error captured from the child process.</summary>
    public string StandardError { get; init; } = string.Empty;
}

/// <summary>
/// Opt-in dev-mode sink that collects fully unsanitized native GDAL invocations for the
/// GP Devkit "glass box" (issue #2128). It exists purely so a developer running the
/// headless <c>honua gp</c> CLI can make the otherwise-opaque native execution fully
/// transparent: the real command (with real scratch paths) and the complete
/// stdout/stderr, none of which the production sanitized path exposes.
/// </summary>
/// <remarks>
/// <para>
/// The production worker host (<c>AddGdalWorker</c>) never registers the capturing
/// command runner, so a <see cref="GlassBoxCapture"/> is only ever populated under the
/// explicit dev opt-in. The class is deliberately a simple, thread-safe collector with
/// no I/O of its own.
/// </para>
/// </remarks>
public sealed class GlassBoxCapture
{
    private readonly object _gate = new();
    private readonly List<GlassBoxCommand> _commands = [];

    /// <summary>The unsanitized command invocations recorded so far, in order.</summary>
    public IReadOnlyList<GlassBoxCommand> Commands
    {
        get
        {
            lock (_gate)
            {
                return _commands.ToArray();
            }
        }
    }

    /// <summary>Records one fully unsanitized invocation. Thread-safe.</summary>
    /// <param name="command">The captured invocation; must not be <c>null</c>.</param>
    public void Record(GlassBoxCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            _commands.Add(command);
        }
    }

    /// <summary>
    /// Shell-quotes a tool + argument list into a single, copy-pasteable command line,
    /// quoting any token that contains whitespace or shell-significant characters so the
    /// emitted "repro locally" hint round-trips through a POSIX shell.
    /// </summary>
    /// <param name="tool">The executable name.</param>
    /// <param name="arguments">The ordered argument list, passed verbatim to the tool.</param>
    /// <returns>A single command-line string.</returns>
    public static string BuildCommandLine(string tool, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(arguments);

        var builder = new StringBuilder(Quote(tool));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string token)
    {
        if (token.Length == 0)
        {
            return "''";
        }

        var needsQuote = token.Any(ch =>
            !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/' or ':' or '=' or '@' or '+' or ','));

        if (!needsQuote)
        {
            return token;
        }

        // Single-quote and escape embedded single quotes the POSIX way: ' -> '\''
        return "'" + token.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
