// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalCommandLog
{
    public const string ToolMetadataKey = "gdal.tool";

    public const string CommandMetadataKey = "gdal.command";

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
