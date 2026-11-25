// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Worker.Gdal.Execution;

internal sealed class BoundedProcessOutputBuffer
{
    public const int DefaultMaxOutputChars = 4 * 1024 * 1024;
    public const int DefaultMaxErrorChars = 64 * 1024;
    public const string StdoutOverflowMessage = "GDAL command standard output exceeded the configured buffer limit.";

    private readonly object _sync = new();
    private readonly StringBuilder _builder = new();
    private readonly int _maxChars;
    private bool _truncated;
    private long _truncatedLineCount;

    public BoundedProcessOutputBuffer(int maxChars = DefaultMaxOutputChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
        _maxChars = maxChars;
    }

    public int MaxChars => _maxChars;

    public int Length
    {
        get
        {
            lock (_sync)
            {
                return _builder.Length;
            }
        }
    }

    public bool IsTruncated
    {
        get
        {
            lock (_sync)
            {
                return _truncated;
            }
        }
    }

    public long TruncatedLineCount
    {
        get
        {
            lock (_sync)
            {
                return _truncatedLineCount;
            }
        }
    }

    public void AppendLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_sync)
        {
            var remaining = _maxChars - _builder.Length;
            if (remaining <= 0)
            {
                _truncated = true;
                _truncatedLineCount++;
                return;
            }

            AppendWithinRemaining(line, remaining);
        }
    }

    public override string ToString()
    {
        lock (_sync)
        {
            return _builder.ToString();
        }
    }

    public static GdalCommandResult BuildCommandResult(
        int exitCode,
        BoundedProcessOutputBuffer stdout,
        BoundedProcessOutputBuffer stderr)
    {
        if (exitCode == 0 && stdout.IsTruncated)
        {
            var existingError = stderr.ToString().Trim();
            var message = string.IsNullOrEmpty(existingError)
                ? StdoutOverflowMessage
                : $"{StdoutOverflowMessage} {existingError}";
            return new GdalCommandResult
            {
                ExitCode = 1,
                StandardOutput = string.Empty,
                StandardError = message,
            };
        }

        return new GdalCommandResult
        {
            ExitCode = exitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
        };
    }

    private void AppendWithinRemaining(string line, int remaining)
    {
        var newline = Environment.NewLine;
        var needed = (long)line.Length + newline.Length;
        if (needed <= remaining)
        {
            _builder.Append(line);
            _builder.Append(newline);
            return;
        }

        var take = Math.Min(line.Length, remaining);
        if (take > 0)
        {
            _builder.Append(line.AsSpan(0, take));
        }

        _truncated = true;
        _truncatedLineCount++;
    }
}
