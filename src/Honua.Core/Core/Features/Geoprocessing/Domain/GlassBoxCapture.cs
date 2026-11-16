// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using System.Text;

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record GlassBoxCommand
{
    public required string Tool { get; init; }

    public required string CommandLine { get; init; }

    public required string WorkingDirectory { get; init; }

    public required int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;
}

public sealed class GlassBoxCapture
{
    private readonly object _gate = new();
    private readonly List<GlassBoxCommand> _commands = [];

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

    public void Record(GlassBoxCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            _commands.Add(command);
        }
    }

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
