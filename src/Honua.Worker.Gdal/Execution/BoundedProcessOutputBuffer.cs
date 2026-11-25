// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Worker.Gdal.Execution;

internal sealed class BoundedProcessOutputBuffer
{
    internal const int DefaultMaxChars = 256 * 1024;
    internal const int DefaultStandardOutputMaxChars = 50 * 1024 * 1024;
    internal const int DefaultMaxErrorChars = 64 * 1024;
    internal const string TruncationMarker = "... [output truncated]";
    internal const string StdoutOverflowMessage = "GDAL command standard output exceeded the configured buffer limit.";

    private enum TruncationMarkerMode
    {
        Append,
        Omit,
    }

    private readonly int _maxChars;
    private readonly TruncationMarkerMode _truncationMarkerMode;
    private readonly Lock _sync = new();
    private readonly StringBuilder _builder = new();
    private bool _truncated;
    private long _truncatedLineCount;

    public BoundedProcessOutputBuffer(int maxChars = DefaultMaxChars)
        : this(maxChars, TruncationMarkerMode.Append)
    {
    }

    private BoundedProcessOutputBuffer(int maxChars, TruncationMarkerMode truncationMarkerMode)
    {
        _maxChars = maxChars > 0
            ? maxChars
            : throw new ArgumentOutOfRangeException(nameof(maxChars), "Maximum character limit must be positive.");
        _truncationMarkerMode = truncationMarkerMode;
    }

    public static BoundedProcessOutputBuffer CreateWithTruncationMarker(int maxChars = DefaultMaxChars)
        => new(maxChars, TruncationMarkerMode.Append);

    public static BoundedProcessOutputBuffer CreateWithoutTruncationMarker(int maxChars = DefaultMaxChars)
        => new(maxChars, TruncationMarkerMode.Omit);

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

    public bool Truncated
    {
        get
        {
            lock (_sync)
            {
                return _truncated;
            }
        }
    }

    public bool IsTruncated => Truncated;

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
            if (_truncated)
            {
                _truncatedLineCount++;
                return;
            }

            AppendWithinLimit(line);
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
        if (exitCode == 0 && stdout.Truncated)
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
                StandardOutputTruncated = true,
            };
        }

        return new GdalCommandResult
        {
            ExitCode = exitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            StandardOutputTruncated = stdout.Truncated,
        };
    }

    private void AppendWithinLimit(string line)
    {
        var remaining = _maxChars - _builder.Length;
        var lineLengthWithNewLine = (long)line.Length + Environment.NewLine.Length;
        if (lineLengthWithNewLine <= remaining)
        {
            _builder.AppendLine(line);
            return;
        }

        if (remaining > 0)
        {
            _builder.Append(line, 0, Math.Min(line.Length, remaining));
        }

        if (_truncationMarkerMode == TruncationMarkerMode.Append)
        {
            _builder.AppendLine();
            _builder.Append(TruncationMarker);
        }

        _truncated = true;
        _truncatedLineCount++;
    }
}
