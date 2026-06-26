// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Emits the GDAL/OGR command a native executor is about to run as a structured log
/// entry on the shared <see cref="IJobExecutionContext"/>. Surfacing the command makes
/// every native run reproducible from the job's logs (operator diagnosis) and is the
/// seam the GP Devkit headless runner reads to report the actual CLI command for a
/// GDAL op (issue #2123). The per-job scratch workspace path is redacted to a stable
/// placeholder so the log does not leak the worker filesystem layout, matching the
/// cross-cutting rule already enforced by <see cref="GdalErrorSanitizer"/>.
/// </summary>
internal static class GdalCommandLog
{
    /// <summary>Metadata key carrying the GDAL/OGR tool name on the command log entry.</summary>
    public const string ToolMetadataKey = "gdal.tool";

    /// <summary>Metadata key carrying the full sanitized command line on the command log entry.</summary>
    public const string CommandMetadataKey = "gdal.command";

    /// <summary>
    /// Appends an informational log entry describing the command (tool + arguments,
    /// with the scratch workspace redacted). Never throws on a null/empty workspace.
    /// </summary>
    public static Task LogCommandAsync(
        IJobExecutionContext context,
        string tool,
        IReadOnlyList<string> arguments,
        string workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);

        var command = BuildSanitizedCommand(tool, arguments, workspace);

        var entry = new ExecutionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = ExecutionLogLevel.Info,
            Message = $"Running GDAL command: {command}",
            Phase = "execute",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ToolMetadataKey] = tool,
                [CommandMetadataKey] = command,
            },
        };

        return context.AppendLogAsync(entry, cancellationToken);
    }

    /// <summary>
    /// Builds the sanitized, single-line command string (<c>tool arg arg ...</c>) with
    /// the per-job scratch workspace path replaced by a stable placeholder.
    /// </summary>
    public static string BuildSanitizedCommand(
        string tool,
        IReadOnlyList<string> arguments,
        string workspace)
    {
        var parts = new List<string>(arguments.Count + 1) { tool };
        foreach (var argument in arguments)
        {
            parts.Add(Redact(argument, workspace));
        }

        return string.Join(' ', parts);
    }

    private static string Redact(string value, string workspace)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(workspace))
        {
            return value;
        }

        return value.Replace(workspace, "<scratch>", StringComparison.Ordinal);
    }
}
